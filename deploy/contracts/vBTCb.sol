// SPDX-License-Identifier: MIT
pragma solidity ^0.8.26;

import "@openzeppelin/contracts-upgradeable/token/ERC20/ERC20Upgradeable.sol";
import "@openzeppelin/contracts-upgradeable/proxy/utils/UUPSUpgradeable.sol";
import "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import "@openzeppelin/contracts/utils/cryptography/MessageHashUtils.sol";

/**
 * @title vBTCb - Verified Bitcoin on Base (Production)
 * @notice Multi-sig verified ERC-20 for vBTC bridged from VFX. No owner: every privileged action needs
 *         signatures from the validator set, and a code upgrade waits upgradeDelay() in public before anyone
 *         can execute it.
 * @dev Deploy behind an ERC1967 UUPS proxy. Initializer seeds the validator registry.
 *
 * V3 Changes:
 * - Replaced burnForExit(amount, lockId) with burnForVfxExit(amount, vfxDestinationAddress)
 *   to support pool-based unlocks (fungible exit — any holder can exit, not just original locker)
 * - Added VfxExitBurned event with vfxDestinationAddress instead of lockId
 *
 * V4 Changes:
 * - Every signed message is EIP-712 typed data under the domain ("vBTCb", "4", chainId, this contract), and each
 *   message type has its own type hash. A signature over one message type can never be read as another (in V3 the
 *   encodePacked ADD/REMOVE messages were also valid mintWithProof arguments).
 * - Upgrades are timelocked: proposeUpgrade, wait upgradeDelay(), then executeUpgrade by anyone.
 *   upgradeWithValidatorApproval is removed.
 * - upgradeDelay() is 48 hours until changed; proposeUpgradeDelay sets it between 48 hours and 30 days, and the
 *   change itself waits the current delay.
 * - Every change to the validator set needs the mint threshold (2/3), so no smaller group can remove the others
 *   and become 2/3 of what is left. A majority can still clear validators that went dark, but only through
 *   proposeRemoval, which waits upgradeDelay() in public.
 * - cancelPending drops whatever is pending (upgrade, delay change, removal). It needs only the blocking minority
 *   (validatorCount - requiredMintSignatures + 1): the validators who could already refuse to sign a mint.
 * - A pending upgrade, delay change or removal lapses PROPOSAL_GRACE_PERIOD (14 days) after it became executable,
 *   and a lapsed one may be replaced, so nothing approved long ago can be run after holders stopped watching.
 * - The validator set stays between 3 and 50; only batch add/remove remain. Each new validator signs its own
 *   addition, so no seat goes to an address whose key nobody holds.
 * - A malformed or unknown signature is skipped instead of reverting the call.
 * - Two groups sign: the validators (registered vBTC validators, up to 50) and the casters (the VFX caster committee,
 *   3 to 10). A mint and every change (either list, an upgrade, the delay) needs 2/3 of the validators AND a majority
 *   of the casters, so neither many validator seats nor the casters alone can mint. A V3 contract upgraded in place
 *   has no casters until updateCasters adds them; until then the caster part is not required.
 * - proposeRemoval also clears casters: with a majority of the validators and of the casters, or with 2/3 of the
 *   validators alone when the casters went dark. Either group's blocking minority cancels whatever is pending.
 *
 * V5 Changes (same EIP-712 domain and MESSAGE_VERSION; message types are only added):
 * - Every function that changes state runs only through the proxy, and no approval is met while the contract holds
 *   fewer than 3 validators, so the implementation contract itself accepts nothing.
 * - With fewer than 3 casters (a V3 proxy upgraded in place) nothing passes except updateCasters adding casters, on 2/3
 *   of the validators and each new caster's own signature, until there are 3. The validators alone never mint.
 * - requiredRemoveSignatures() is computed from the validator count; its V3 storage slot is retired.
 * - Each seat records the admin nonce it was given at. A pending removal leaves out a validator or caster seated after
 *   it was proposed, so a member removed and admitted again is not removed by the older proposal.
 * - proposeCasterReplacement: 2/3 of the validators replace casters that went dark (too few left to approve
 *   updateCasters), each new caster signing its own seat. It waits upgradeDelay() as a pending removal, and either
 *   group's blocking minority cancels it, so casters that are still live can always stop it. A removal proposed before
 *   V5 is not executed (V4 kept no seat nonces) and may be proposed again.
 * - cancelPendingKinds drops only the kinds it names (upgrade, delay change, removal), so vetoing a removal leaves a
 *   pending upgrade alone. cancelPending still drops all three.
 * - burnForVfxExit takes only a VFX address of this network. burnForBTCExit takes at least MIN_BTC_EXIT_SATS, the
 *   smallest part a VFX vault pays in an exit to Bitcoin. burnForBTCExitWithMinPayout also names the least the holder
 *   accepts after Bitcoin fees.
 * - refundBurn mints back, once per burn, a burn that VFX recorded it cannot pay, with the mint approval.
 */
contract vBTCb is ERC20Upgradeable, UUPSUpgradeable {
    mapping(address => bool) public isValidator;
    address[] public validators;
    uint256 public validatorCount;
    uint256 public requiredMintSignatures;
    /// @dev V3's stored removal threshold; requiredRemoveSignatures() is computed since V5. Slot kept, never read.
    /// @custom:oz-renamed-from requiredRemoveSignatures
    uint256 private _retiredRemoveThreshold;

    uint256 public constant MIN_REQUIRED_SIGNATURES = 2;
    uint256 public constant MIN_VALIDATORS = 3;
    uint256 public constant MAX_VALIDATORS = 50;
    uint256 public constant MIN_CASTERS = 3;
    uint256 public constant MAX_CASTERS = 10;
    uint256 public constant DEFAULT_UPGRADE_DELAY = 48 hours;
    uint256 public constant MAX_UPGRADE_DELAY = 30 days;
    /// @notice A proposal that is not executed within this long after it became executable lapses.
    uint256 public constant PROPOSAL_GRACE_PERIOD = 14 days;
    uint256 public constant MESSAGE_VERSION = 4;
    /// @notice The smallest burnForBTCExit: the smallest part a VFX vault pays in an exit to Bitcoin (VFX's
    ///         BtcWithdrawals.MinExitPartSats). VFX refuses any smaller exit, so a smaller burn could never be paid.
    uint256 public constant MIN_BTC_EXIT_SATS = 25_186;
    /// @notice Kinds for cancelPendingKinds, combined as bits.
    uint256 public constant PENDING_UPGRADE = 1;
    uint256 public constant PENDING_UPGRADE_DELAY = 2;
    uint256 public constant PENDING_REMOVAL = 4;

    bytes32 private constant DOMAIN_TYPEHASH =
        keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");
    bytes32 private constant DOMAIN_NAME_HASH = keccak256("vBTCb");
    bytes32 private constant DOMAIN_VERSION_HASH = keccak256("4");

    bytes32 private constant MINT_TYPEHASH =
        keccak256("Mint(address to,uint256 amount,string lockId,uint256 nonce)");
    bytes32 private constant ADD_VALIDATORS_TYPEHASH =
        keccak256("AddValidators(address[] validators,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 private constant REMOVE_VALIDATORS_TYPEHASH =
        keccak256("RemoveValidators(address[] validators,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 private constant PROPOSE_UPGRADE_TYPEHASH =
        keccak256("ProposeUpgrade(address implementation,uint256 adminNonce)");
    bytes32 private constant PROPOSE_UPGRADE_DELAY_TYPEHASH =
        keccak256("ProposeUpgradeDelay(uint256 delay,uint256 adminNonce)");
    bytes32 private constant PROPOSE_REMOVAL_TYPEHASH =
        keccak256("ProposeRemoval(address[] validators,address[] casters,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 private constant UPDATE_CASTERS_TYPEHASH =
        keccak256("UpdateCasters(address[] add,address[] remove,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 private constant CANCEL_PENDING_TYPEHASH =
        keccak256("CancelPending(uint256 adminNonce)");
    bytes32 private constant CANCEL_PENDING_KINDS_TYPEHASH =
        keccak256("CancelPendingKinds(uint256 kinds,uint256 adminNonce)");
    bytes32 private constant REFUND_TYPEHASH =
        keccak256("Refund(address to,uint256 amount,string burnId)");
    bytes32 private constant REPLACE_CASTERS_TYPEHASH =
        keccak256("ReplaceCasters(address[] remove,address[] add,uint256 vfxBlockHeight,uint256 adminNonce)");

    // Reverts (custom errors keep the contract under Base's code size limit).
    error AlreadyACaster();
    error AlreadyAValidator();
    error BelowSmallestExit();
    error BurnAlreadyRefunded();
    error CasterCountOutOfRange();
    error CastersBelowMinimum();
    error CastersNotSetUp();
    error DelayChangeAlreadyPending();
    error DelayNotElapsed();
    error DelayOutOfRange();
    error DuplicateCaster();
    error DuplicateInBatch();
    error DuplicateValidator();
    error EmptyBatch();
    error InsufficientSignatures();
    error InvalidAddress();
    error InvalidBTCAddress();
    error InvalidCasterAddress();
    error InvalidImplementation();
    error InvalidRecipient();
    error InvalidValidatorAddress();
    error LockIdAlreadyUsed();
    error MinPayoutAboveAmount();
    error NeedAtLeast3Validators();
    error NewCasterDidNotSign();
    error NewValidatorDidNotSign();
    error NoDelayChangePending();
    error NoRemovalPending();
    error NoUpgradePending();
    error NotACaster();
    error NotAVFXAddress();
    error NotAValidator();
    error NothingPending();
    error OneSignaturePerNewCaster();
    error OneSignaturePerNewValidator();
    error OnlyAdditionsUntil3Casters();
    error ProposalLapsed();
    error ProposedBeforeV5();
    error RemovalAlreadyPending();
    error TooManyValidators();
    error UnknownKinds();
    error UpgradeAlreadyPending();
    error UseProposeUpgrade();
    error VFXDestinationRequired();
    error ValidatorsBelowMinimum();
    error ZeroAmount();

    mapping(bytes32 => bool) public usedLockIds;
    uint256 public adminNonce;

    event MintExecuted(address indexed to, uint256 amount, string lockId, uint256 nonce);
    event VfxExitBurned(address indexed burner, uint256 amount, string vfxDestinationAddress, uint256 chainId);
    event BTCExitBurned(address indexed burner, uint256 amount, string btcDestination, uint256 chainId);
    event ValidatorBatchAdded(address[] validators, uint256 vfxBlockHeight);
    event ValidatorBatchRemoved(address[] validators, uint256 vfxBlockHeight);
    event ContractUpgraded(address indexed newImplementation, uint256 adminNonce);
    event UpgradeProposed(address indexed newImplementation, uint256 eta, uint256 adminNonce);
    event UpgradeDelayProposed(uint256 newDelay, uint256 eta, uint256 adminNonce);
    event UpgradeDelayChanged(uint256 newDelay);
    event RemovalProposed(address[] validators, address[] casters, uint256 vfxBlockHeight, uint256 eta, uint256 adminNonce);
    event CastersUpdated(address[] added, address[] removed, uint256 vfxBlockHeight);
    event PendingCancelled(address implementation, uint256 upgradeDelay, uint256 removals, uint256 adminNonce);
    /// @notice Emitted after BTCExitBurned by burnForBTCExitWithMinPayout: the least the holder accepts after Bitcoin fees.
    event BTCExitMinPayout(address indexed burner, uint256 minPayoutSats);
    event BurnRefunded(address indexed to, uint256 amount, string burnId);
    /// @notice Emitted after RemovalProposed by proposeCasterReplacement: the casters seated when the removal is executed.
    event CasterAdditionsProposed(address[] casters);

    bool private _upgradeAuthorized;

    // V4 storage: appended after V3's last variable and taken out of __gap.
    address public pendingImplementation;
    uint256 public pendingUpgradeEta;
    uint256 private _upgradeDelay;
    uint256 public pendingUpgradeDelay;
    uint256 public pendingUpgradeDelayEta;
    address[] private _pendingRemovals;
    uint256 public pendingRemovalHeight;
    uint256 public pendingRemovalEta;
    mapping(address => bool) public isCaster;
    address[] private _casters;
    address[] private _pendingCasterRemovals;

    // V5 storage: appended after V4's and taken out of __gap.
    /// @notice adminNonce + 1 at the validator's latest admission (0: seated before V5 or at initialize).
    mapping(address => uint256) public validatorSince;
    /// @notice adminNonce + 1 at the caster's latest admission (0: seated before V5 or at initialize).
    mapping(address => uint256) public casterSince;
    /// @notice adminNonce + 1 when the pending removal was proposed (0: none, or proposed before V5); a seat given at that
    ///         nonce or later is left out of the removal.
    uint256 public pendingRemovalNonce;
    /// @notice Burns already refunded, by keccak256 of the burn's id as VFX names it (isBurnRefunded reads it by the id).
    mapping(bytes32 => bool) public refundedBurns;
    /// @notice Casters the pending removal seats once executed (proposeCasterReplacement).
    address[] private _pendingCasterAdds;

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(
        string memory name_,
        string memory symbol_,
        address[] memory initialValidators,
        address[] memory initialCasters
    ) public initializer {
        __ERC20_init(name_, symbol_);

        require(initialValidators.length >= MIN_VALIDATORS, NeedAtLeast3Validators());
        require(initialValidators.length <= MAX_VALIDATORS, TooManyValidators());

        for (uint256 i = 0; i < initialValidators.length; i++) {
            require(initialValidators[i] != address(0), InvalidValidatorAddress());
            require(!isValidator[initialValidators[i]], DuplicateValidator());
            isValidator[initialValidators[i]] = true;
            validators.push(initialValidators[i]);
        }

        validatorCount = initialValidators.length;
        _recalculateThresholds();

        require(initialCasters.length >= MIN_CASTERS && initialCasters.length <= MAX_CASTERS, CasterCountOutOfRange());
        for (uint256 i = 0; i < initialCasters.length; i++) {
            require(initialCasters[i] != address(0), InvalidCasterAddress());
            require(!isCaster[initialCasters[i]], DuplicateCaster());
            isCaster[initialCasters[i]] = true;
            _casters.push(initialCasters[i]);
        }
    }

    function decimals() public pure override returns (uint8) {
        return 8;
    }

    function mintWithProof(
        address to,
        uint256 amount,
        string calldata lockId,
        uint256 nonce,
        bytes[] calldata signatures
    ) external onlyProxy {
        require(to != address(0), InvalidRecipient());
        require(amount > 0, ZeroAmount());

        bytes32 lockIdHash = keccak256(bytes(lockId));
        require(!usedLockIds[lockIdHash], LockIdAlreadyUsed());

        bytes32 digest = _hashTypedData(keccak256(abi.encode(MINT_TYPEHASH, to, amount, lockIdHash, nonce)));
        _requireApproval(digest, signatures);

        usedLockIds[lockIdHash] = true;
        _mint(to, amount);

        emit MintExecuted(to, amount, lockId, nonce);
    }

    /**
     * @notice Burn vBTC.b tokens to exit back to VFX chain.
     * @dev Pool-based unlock: burner specifies a VFX destination address.
     *      The VFX network will select available bridge locks FIFO to fulfill the exit,
     *      crediting vBTC from one or more contracts to the destination address.
     *      The destination must be a VFX address of this network as VFX nodes take one (see _isVfxAddress): the burn
     *      cannot be undone, and VFX credits nothing to any other string.
     * @param amount Amount of vBTC.b to burn (8 decimals, in sats)
     * @param vfxDestinationAddress The VFX address to receive the unlocked vBTC
     */
    function burnForVfxExit(uint256 amount, string calldata vfxDestinationAddress) external onlyProxy {
        require(amount > 0, ZeroAmount());
        require(bytes(vfxDestinationAddress).length > 0, VFXDestinationRequired());
        require(_isVfxAddress(vfxDestinationAddress), NotAVFXAddress());
        _burn(msg.sender, amount);
        emit VfxExitBurned(msg.sender, amount, vfxDestinationAddress, block.chainid);
    }

    /**
     * @notice Burn vBTC.b tokens to exit directly to a BTC address.
     * @dev The VFX network FROST-signs a BTC transaction to the specified destination. Bitcoin fees come out of the
     *      amount, so the payout is less than it.
     * @param amount Amount of vBTC.b to burn (8 decimals, in sats), at least MIN_BTC_EXIT_SATS
     * @param btcDestination The Bitcoin address to send BTC to
     */
    function burnForBTCExit(uint256 amount, string calldata btcDestination) external onlyProxy {
        _burnForBTCExit(amount, btcDestination);
    }

    /**
     * @notice burnForBTCExit with the least the holder accepts: VFX pays no Bitcoin transaction that would send less to
     *         btcDestination, and an exit it cannot pay at or above it is refunded instead (refundBurn).
     * @param minPayoutSats at most amount
     */
    function burnForBTCExitWithMinPayout(uint256 amount, string calldata btcDestination, uint256 minPayoutSats) external onlyProxy {
        require(minPayoutSats <= amount, MinPayoutAboveAmount());
        _burnForBTCExit(amount, btcDestination);
        emit BTCExitMinPayout(msg.sender, minPayoutSats);
    }

    function _burnForBTCExit(uint256 amount, string calldata btcDestination) internal {
        require(amount >= MIN_BTC_EXIT_SATS, BelowSmallestExit());
        require(bytes(btcDestination).length >= 26, InvalidBTCAddress());
        _burn(msg.sender, amount);
        emit BTCExitBurned(msg.sender, amount, btcDestination, block.chainid);
    }

    /**
     * @notice Mints back a burn VFX recorded it cannot pay (an exit below what it can pay, or one that ran out of ways to
     *         be paid), once per burn, with the mint approval: 2/3 of the validators and a majority of the casters.
     * @param burnId the burn's id as VFX names it: its Base transaction hash, with ":n" for the n-th burn of a transaction
     *        that holds several
     */
    function refundBurn(address to, uint256 amount, string calldata burnId, bytes[] calldata signatures) external onlyProxy {
        require(to != address(0), InvalidRecipient());
        require(amount > 0, ZeroAmount());
        bytes32 burnIdHash = keccak256(bytes(burnId));
        require(!refundedBurns[burnIdHash], BurnAlreadyRefunded());

        bytes32 digest = _hashTypedData(keccak256(abi.encode(REFUND_TYPEHASH, to, amount, burnIdHash)));
        _requireApproval(digest, signatures);

        refundedBurns[burnIdHash] = true;
        _mint(to, amount);
        emit BurnRefunded(to, amount, burnId);
    }

    function isBurnRefunded(string calldata burnId) external view returns (bool) {
        return refundedBurns[keccak256(bytes(burnId))];
    }

    /**
     * @param newValidatorSignatures one per new validator, in the same order: its own signature over the same
     *        AddValidators message. A seat goes only to an address whose key is held, so the set cannot be filled with
     *        addresses nobody can sign for (enough of those would freeze every threshold for good).
     */
    function addValidatorBatch(
        address[] calldata newValidators,
        uint256 vfxBlockHeight,
        bytes[] calldata signatures,
        bytes[] calldata newValidatorSignatures
    ) external onlyProxy {
        require(newValidators.length > 0, EmptyBatch());
        require(validatorCount + newValidators.length <= MAX_VALIDATORS, TooManyValidators());
        require(newValidatorSignatures.length == newValidators.length, OneSignaturePerNewValidator());

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            ADD_VALIDATORS_TYPEHASH, keccak256(abi.encodePacked(newValidators)), vfxBlockHeight, adminNonce)));
        _requireApproval(digest, signatures);

        for (uint256 i = 0; i < newValidators.length; i++) {
            require(newValidators[i] != address(0), InvalidAddress());
            require(!isValidator[newValidators[i]], AlreadyAValidator()); // also rejects a repeat within the batch
            (address own, ECDSA.RecoverError error, ) = ECDSA.tryRecover(digest, newValidatorSignatures[i]);
            require(error == ECDSA.RecoverError.NoError && own == newValidators[i], NewValidatorDidNotSign());

            isValidator[newValidators[i]] = true;
            validatorSince[newValidators[i]] = adminNonce + 1;
            validators.push(newValidators[i]);
        }

        validatorCount += newValidators.length;
        adminNonce++;
        _recalculateThresholds();

        emit ValidatorBatchAdded(newValidators, vfxBlockHeight);
    }

    function removeValidatorBatch(address[] calldata oldValidators, uint256 vfxBlockHeight, bytes[] calldata signatures) external onlyProxy {
        require(oldValidators.length > 0, EmptyBatch());
        require(oldValidators.length <= validatorCount && validatorCount - oldValidators.length >= MIN_VALIDATORS,
            ValidatorsBelowMinimum());

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            REMOVE_VALIDATORS_TYPEHASH, keccak256(abi.encodePacked(oldValidators)), vfxBlockHeight, adminNonce)));
        // The mint approval: with less, a group could remove the rest and be 2/3 of the set that is left.
        _requireApproval(digest, signatures);

        adminNonce++;
        _removeValidators(oldValidators, vfxBlockHeight);
    }

    /**
     * @notice Clears validators or casters that went dark (too many for the approval to be reached). Needs a majority of
     *         the validators and of the casters, or 2/3 of the validators alone (the casters are the ones gone). The
     *         removal waits upgradeDelay() and either group's blocking minority can cancel it meanwhile, so neither
     *         group can use it to push the other out while the other is watching.
     */
    function proposeRemoval(
        address[] calldata oldValidators,
        address[] calldata oldCasters,
        uint256 vfxBlockHeight,
        bytes[] calldata signatures
    ) external onlyProxy {
        require(oldValidators.length + oldCasters.length > 0, EmptyBatch());
        require(oldValidators.length <= validatorCount && validatorCount - oldValidators.length >= MIN_VALIDATORS,
            ValidatorsBelowMinimum());
        require(oldCasters.length == 0 || (oldCasters.length <= _casters.length && _casters.length - oldCasters.length >= MIN_CASTERS),
            CastersBelowMinimum());
        _requireRemovalSlotFree();

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            PROPOSE_REMOVAL_TYPEHASH, keccak256(abi.encodePacked(oldValidators)), keccak256(abi.encodePacked(oldCasters)),
            vfxBlockHeight, adminNonce)));
        (uint256 v, uint256 c) = _countSigners(digest, signatures, requiredMintSignatures, requiredCasterSignatures());
        require((v >= _validatorMajority() && c >= requiredCasterSignatures()) || v >= requiredMintSignatures,
            InsufficientSignatures());

        for (uint256 i = 0; i < oldValidators.length; i++) {
            require(isValidator[oldValidators[i]], NotAValidator());
            for (uint256 j = 0; j < i; j++) require(oldValidators[j] != oldValidators[i], DuplicateInBatch());
        }
        for (uint256 i = 0; i < oldCasters.length; i++) {
            require(isCaster[oldCasters[i]], NotACaster());
            for (uint256 j = 0; j < i; j++) require(oldCasters[j] != oldCasters[i], DuplicateInBatch());
        }

        _pendingRemovals = oldValidators;
        _setPendingRemoval(oldCasters, new address[](0), vfxBlockHeight);
        emit RemovalProposed(oldValidators, oldCasters, vfxBlockHeight, pendingRemovalEta, adminNonce);
        adminNonce++;
    }

    /**
     * @notice Replaces casters that went dark when too few are left to approve updateCasters: 2/3 of the validators name
     *         the casters to remove and those to seat, and each new caster signs its own seat. Like proposeRemoval it waits
     *         upgradeDelay() in public, and either group's blocking minority can cancel it meanwhile, so live casters
     *         can always stop their own replacement. Executed by executeRemoval; the list stays between 3 and 10.
     * @param newCasterSignatures one per added caster, in the same order, over the same ReplaceCasters message.
     */
    function proposeCasterReplacement(
        address[] calldata remove,
        address[] calldata add,
        uint256 vfxBlockHeight,
        bytes[] calldata signatures,
        bytes[] calldata newCasterSignatures
    ) external onlyProxy {
        // Never more seats than it clears: the validators alone cannot grow the list into a caster majority of their own.
        require(remove.length > 0 && add.length <= remove.length && remove.length <= _casters.length, CasterCountOutOfRange());
        require(newCasterSignatures.length == add.length, OneSignaturePerNewCaster());
        require(_casters.length - remove.length + add.length >= MIN_CASTERS, CasterCountOutOfRange());
        _requireRemovalSlotFree();

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            REPLACE_CASTERS_TYPEHASH, keccak256(abi.encodePacked(remove)), keccak256(abi.encodePacked(add)),
            vfxBlockHeight, adminNonce)));
        (uint256 v, ) = _countSigners(digest, signatures, requiredMintSignatures, 0);
        require(v >= requiredMintSignatures, InsufficientSignatures());

        for (uint256 i = 0; i < remove.length; i++) {
            require(isCaster[remove[i]], NotACaster());
            for (uint256 j = 0; j < i; j++) require(remove[j] != remove[i], DuplicateInBatch());
        }
        for (uint256 i = 0; i < add.length; i++) {
            require(add[i] != address(0), InvalidAddress());
            require(!isCaster[add[i]], AlreadyACaster());
            for (uint256 j = 0; j < i; j++) require(add[j] != add[i], DuplicateInBatch());
            (address own, ECDSA.RecoverError error, ) = ECDSA.tryRecover(digest, newCasterSignatures[i]);
            require(error == ECDSA.RecoverError.NoError && own == add[i], NewCasterDidNotSign());
        }

        delete _pendingRemovals;
        _setPendingRemoval(remove, add, vfxBlockHeight);
        emit RemovalProposed(new address[](0), remove, vfxBlockHeight, pendingRemovalEta, adminNonce);
        emit CasterAdditionsProposed(add);
        adminNonce++;
    }

    /// @dev A removal may be proposed when none is pending, the pending one lapsed, or it was proposed before V5 (which
    ///      cannot be executed: V4 kept no seat nonces). Not while there are fewer than 3 casters.
    function _requireRemovalSlotFree() internal view {
        require(pendingRemovalEta == 0 || _lapsed(pendingRemovalEta) || pendingRemovalNonce == 0, RemovalAlreadyPending());
        require(_casters.length >= MIN_CASTERS, CastersNotSetUp());
    }

    function _setPendingRemoval(address[] memory casters, address[] memory adds, uint256 vfxBlockHeight) internal {
        _pendingCasterRemovals = casters;
        _pendingCasterAdds = adds;
        pendingRemovalHeight = vfxBlockHeight;
        pendingRemovalEta = block.timestamp + upgradeDelay();
        pendingRemovalNonce = adminNonce + 1;
    }

    /// @notice Anyone may carry out a proposed removal once its delay has passed. A member seated again after the
    ///         proposal is left out of it. A removal proposed before V5 is not carried out: propose it again.
    function executeRemoval() external onlyProxy {
        require(pendingRemovalEta != 0, NoRemovalPending());
        require(pendingRemovalNonce != 0, ProposedBeforeV5());
        require(block.timestamp >= pendingRemovalEta, DelayNotElapsed());
        require(!_lapsed(pendingRemovalEta), ProposalLapsed());

        // Skip any already removed since the proposal (by the 2/3 path), and any seated again after it.
        address[] memory list = _stillRemovable(_pendingRemovals, false);
        address[] memory casterList = _stillRemovable(_pendingCasterRemovals, true);
        address[] memory adds = _pendingCasterAdds;
        uint256 pendingNonce = pendingRemovalNonce;
        uint256 n = list.length;
        uint256 m = casterList.length;
        uint256 height = pendingRemovalHeight;
        delete _pendingRemovals;
        _clearPendingRemoval();

        require(validatorCount - n >= MIN_VALIDATORS, ValidatorsBelowMinimum());
        if (n > 0) _removeValidators(list, height);
        if (m + adds.length > 0) {
            for (uint256 i = 0; i < m; i++) _removeCaster(casterList[i]);
            // Seated as by updateCasters; one seated meanwhile is kept as it is. Never more than the casters this removes,
            // whatever the casters changed during the delay.
            uint256 k = 0;
            for (uint256 i = 0; i < adds.length && k < m; i++) {
                // Seated meanwhile (and perhaps removed again since) by updateCasters: left as the casters made it.
                if (isCaster[adds[i]] || casterSince[adds[i]] >= pendingNonce) continue;
                isCaster[adds[i]] = true;
                casterSince[adds[i]] = adminNonce + 1;
                _casters.push(adds[i]);
                adds[k++] = adds[i];
            }
            assembly ("memory-safe") { mstore(adds, k) } // only those seated here
            require(_casters.length >= MIN_CASTERS && _casters.length <= MAX_CASTERS, CasterCountOutOfRange());
            if (k > 0) adminNonce++;
            emit CastersUpdated(adds, casterList, height);
        }
    }

    function _clearPendingRemoval() internal {
        delete _pendingCasterRemovals;
        delete _pendingCasterAdds;
        pendingRemovalHeight = 0;
        pendingRemovalEta = 0;
        pendingRemovalNonce = 0;
    }

    /// @dev The members of `pending` still seated and not seated again after the pending removal was proposed.
    function _stillRemovable(address[] storage pending, bool casters) internal view returns (address[] memory kept) {
        kept = new address[](pending.length);
        uint256 n = 0;
        for (uint256 i = 0; i < pending.length; i++) {
            address a = pending[i];
            bool seated = casters ? isCaster[a] : isValidator[a];
            uint256 since = casters ? casterSince[a] : validatorSince[a];
            if (seated && since < pendingRemovalNonce) kept[n++] = a;
        }
        assembly ("memory-safe") { mstore(kept, n) } // trim to the n kept
    }

    function getPendingRemovals() external view returns (address[] memory) {
        return _pendingRemovals;
    }

    function getPendingCasterRemovals() external view returns (address[] memory) {
        return _pendingCasterRemovals;
    }

    function getPendingCasterAdds() external view returns (address[] memory) {
        return _pendingCasterAdds;
    }

    /**
     * @notice Brings the caster list in step with the VFX caster committee: removals, then additions, in one call.
     *         While the contract has fewer than 3 casters (a V3 proxy upgraded in place) it only adds, on 2/3 of the
     *         validators, and must reach 3: nothing else is approved until it has.
     * @param newCasterSignatures one per added caster, in the same order: its own signature over the same UpdateCasters
     *        message, so no seat goes to an address whose key nobody holds.
     */
    function updateCasters(
        address[] calldata add,
        address[] calldata remove,
        uint256 vfxBlockHeight,
        bytes[] calldata signatures,
        bytes[] calldata newCasterSignatures
    ) external onlyProxy {
        require(add.length + remove.length > 0, EmptyBatch());
        require(newCasterSignatures.length == add.length, OneSignaturePerNewCaster());

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            UPDATE_CASTERS_TYPEHASH, keccak256(abi.encodePacked(add)), keccak256(abi.encodePacked(remove)), vfxBlockHeight, adminNonce)));
        if (_casters.length < MIN_CASTERS) {
            // The bootstrap: there are no casters to approve, so 2/3 of the validators add them, each signing its own seat.
            require(remove.length == 0, OnlyAdditionsUntil3Casters());
            (uint256 v, ) = _countSigners(digest, signatures, requiredMintSignatures, 0);
            require(v >= requiredMintSignatures, InsufficientSignatures());
        } else {
            _requireApproval(digest, signatures);
        }

        for (uint256 i = 0; i < remove.length; i++) {
            require(isCaster[remove[i]], NotACaster()); // also rejects a repeat within the batch
            _removeCaster(remove[i]);
        }
        for (uint256 i = 0; i < add.length; i++) {
            require(add[i] != address(0), InvalidAddress());
            require(!isCaster[add[i]], AlreadyACaster()); // also rejects a repeat within the batch
            (address own, ECDSA.RecoverError error, ) = ECDSA.tryRecover(digest, newCasterSignatures[i]);
            require(error == ECDSA.RecoverError.NoError && own == add[i], NewCasterDidNotSign());
            isCaster[add[i]] = true;
            casterSince[add[i]] = adminNonce + 1;
            _casters.push(add[i]);
        }
        require(_casters.length >= MIN_CASTERS && _casters.length <= MAX_CASTERS, CasterCountOutOfRange());

        adminNonce++;
        emit CastersUpdated(add, remove, vfxBlockHeight);
    }

    function _removeCaster(address caster) internal {
        isCaster[caster] = false;
        for (uint256 j = 0; j < _casters.length; j++) {
            if (_casters[j] == caster) {
                _casters[j] = _casters[_casters.length - 1];
                _casters.pop();
                break;
            }
        }
    }

    function _removeValidators(address[] memory oldValidators, uint256 vfxBlockHeight) internal {
        for (uint256 i = 0; i < oldValidators.length; i++) {
            require(isValidator[oldValidators[i]], NotAValidator()); // also rejects a repeat within the batch

            isValidator[oldValidators[i]] = false;

            for (uint256 j = 0; j < validators.length; j++) {
                if (validators[j] == oldValidators[i]) {
                    validators[j] = validators[validators.length - 1];
                    validators.pop();
                    break;
                }
            }
        }

        validatorCount -= oldValidators.length;
        _recalculateThresholds();

        emit ValidatorBatchRemoved(oldValidators, vfxBlockHeight);
    }

    /// @notice How long a proposed upgrade, delay change or removal waits before it can be executed.
    function upgradeDelay() public view returns (uint256) {
        return _upgradeDelay == 0 ? DEFAULT_UPGRADE_DELAY : _upgradeDelay;
    }

    function proposeUpgrade(address newImplementation, bytes[] calldata signatures) external onlyProxy {
        require(newImplementation.code.length > 0, InvalidImplementation());
        require(pendingImplementation == address(0) || _lapsed(pendingUpgradeEta), UpgradeAlreadyPending());

        bytes32 digest = _hashTypedData(keccak256(abi.encode(PROPOSE_UPGRADE_TYPEHASH, newImplementation, adminNonce)));
        _requireApproval(digest, signatures);

        pendingImplementation = newImplementation;
        pendingUpgradeEta = block.timestamp + upgradeDelay();

        emit UpgradeProposed(newImplementation, pendingUpgradeEta, adminNonce);
        adminNonce++;
    }

    /// @notice Anyone may execute a proposed upgrade once its delay has passed. No call data is passed to it.
    function executeUpgrade() external onlyProxy {
        address impl = pendingImplementation;
        require(impl != address(0), NoUpgradePending());
        require(block.timestamp >= pendingUpgradeEta, DelayNotElapsed());
        require(!_lapsed(pendingUpgradeEta), ProposalLapsed());

        pendingImplementation = address(0);
        pendingUpgradeEta = 0;

        _upgradeAuthorized = true;
        upgradeToAndCall(impl, new bytes(0));
        emit ContractUpgraded(impl, adminNonce);
    }

    function proposeUpgradeDelay(uint256 newDelay, bytes[] calldata signatures) external onlyProxy {
        require(newDelay >= DEFAULT_UPGRADE_DELAY && newDelay <= MAX_UPGRADE_DELAY, DelayOutOfRange());
        require(pendingUpgradeDelayEta == 0 || _lapsed(pendingUpgradeDelayEta), DelayChangeAlreadyPending());

        bytes32 digest = _hashTypedData(keccak256(abi.encode(PROPOSE_UPGRADE_DELAY_TYPEHASH, newDelay, adminNonce)));
        _requireApproval(digest, signatures);

        pendingUpgradeDelay = newDelay;
        pendingUpgradeDelayEta = block.timestamp + upgradeDelay();

        emit UpgradeDelayProposed(newDelay, pendingUpgradeDelayEta, adminNonce);
        adminNonce++;
    }

    function executeUpgradeDelay() external onlyProxy {
        require(pendingUpgradeDelayEta != 0, NoDelayChangePending());
        require(block.timestamp >= pendingUpgradeDelayEta, DelayNotElapsed());
        require(!_lapsed(pendingUpgradeDelayEta), ProposalLapsed());

        _upgradeDelay = pendingUpgradeDelay;
        pendingUpgradeDelay = 0;
        pendingUpgradeDelayEta = 0;

        emit UpgradeDelayChanged(_upgradeDelay);
    }

    /// @dev A strict majority of the validators, from the count itself.
    function _validatorMajority() internal view returns (uint256) {
        return _max(MIN_REQUIRED_SIGNATURES, validatorCount / 2 + 1);
    }

    /// @notice The validators proposeRemoval needs with a majority of the casters: a strict majority, from the count.
    function requiredRemoveSignatures() public view returns (uint256) {
        return _validatorMajority();
    }

    /// @notice The validators who could refuse a mint: validatorCount - requiredMintSignatures + 1.
    function requiredVetoSignatures() public view returns (uint256) {
        return validatorCount - requiredMintSignatures + 1;
    }

    /// @notice A majority of the casters (0 while the contract has none, after an upgrade from V3: then nothing but the
    ///         caster bootstrap in updateCasters is approved).
    function requiredCasterSignatures() public view returns (uint256) {
        return _casters.length == 0 ? 0 : _casters.length / 2 + 1;
    }

    /// @notice The casters who could refuse a mint: casterCount - requiredCasterSignatures + 1 (0: no casters, no caster veto).
    function requiredCasterVetoSignatures() public view returns (uint256) {
        return _casters.length == 0 ? 0 : _casters.length - requiredCasterSignatures() + 1;
    }

    /// @notice Either group's blocking minority drops whatever is pending: an upgrade, a delay change and a removal.
    function cancelPending(bytes[] calldata signatures) external onlyProxy {
        require(pendingImplementation != address(0) || pendingUpgradeDelayEta != 0 || pendingRemovalEta != 0, NothingPending());
        _requireVeto(_hashTypedData(keccak256(abi.encode(CANCEL_PENDING_TYPEHASH, adminNonce))), signatures);
        _cancel(PENDING_UPGRADE | PENDING_UPGRADE_DELAY | PENDING_REMOVAL);
    }

    /// @notice Either group's blocking minority drops the pending kinds it names (PENDING_UPGRADE, PENDING_UPGRADE_DELAY,
    ///         PENDING_REMOVAL as bits) and leaves the others pending.
    function cancelPendingKinds(uint256 kinds, bytes[] calldata signatures) external onlyProxy {
        require(kinds != 0 && kinds <= (PENDING_UPGRADE | PENDING_UPGRADE_DELAY | PENDING_REMOVAL), UnknownKinds());
        require((kinds & PENDING_UPGRADE != 0 && pendingImplementation != address(0))
            || (kinds & PENDING_UPGRADE_DELAY != 0 && pendingUpgradeDelayEta != 0)
            || (kinds & PENDING_REMOVAL != 0 && pendingRemovalEta != 0), NothingPending());
        _requireVeto(_hashTypedData(keccak256(abi.encode(CANCEL_PENDING_KINDS_TYPEHASH, kinds, adminNonce))), signatures);
        _cancel(kinds);
    }

    /// @dev Either group's blocking minority: requiredVetoSignatures() validators or requiredCasterVetoSignatures() casters.
    function _requireVeto(bytes32 digest, bytes[] calldata signatures) internal view {
        uint256 validatorVeto = requiredVetoSignatures();
        uint256 casterVeto = requiredCasterVetoSignatures();
        (uint256 v, uint256 c) = _countSigners(digest, signatures, validatorVeto, casterVeto == 0 ? type(uint256).max : casterVeto);
        require(v >= validatorVeto || (casterVeto > 0 && c >= casterVeto), InsufficientSignatures());
    }

    function _cancel(uint256 kinds) internal {
        bool upgrade = kinds & PENDING_UPGRADE != 0;
        bool delay = kinds & PENDING_UPGRADE_DELAY != 0;
        bool removal = kinds & PENDING_REMOVAL != 0;
        emit PendingCancelled(upgrade ? pendingImplementation : address(0), delay ? pendingUpgradeDelay : 0,
            removal ? _pendingRemovals.length + _pendingCasterRemovals.length : 0, adminNonce);

        if (upgrade) {
            pendingImplementation = address(0);
            pendingUpgradeEta = 0;
        }
        if (delay) {
            pendingUpgradeDelay = 0;
            pendingUpgradeDelayEta = 0;
        }
        if (removal) {
            delete _pendingRemovals;
            _clearPendingRemoval();
        }
        adminNonce++;
    }

    function getValidators() external view returns (address[] memory) {
        return validators;
    }

    function getCasters() external view returns (address[] memory) {
        return _casters;
    }

    function casterCount() external view returns (uint256) {
        return _casters.length;
    }

    function getAdminNonce() external view returns (uint256) {
        return adminNonce;
    }

    function isLockIdUsed(string calldata lockId) external view returns (bool) {
        return usedLockIds[keccak256(abi.encodePacked(lockId))];
    }

    /// @notice EIP-712 domain separator for this contract on this chain.
    function DOMAIN_SEPARATOR() public view returns (bytes32) {
        return keccak256(abi.encode(DOMAIN_TYPEHASH, DOMAIN_NAME_HASH, DOMAIN_VERSION_HASH, block.chainid, address(this)));
    }

    function _hashTypedData(bytes32 structHash) internal view returns (bytes32) {
        return MessageHashUtils.toTypedDataHash(DOMAIN_SEPARATOR(), structHash);
    }

    function _recalculateThresholds() internal {
        // Mints ALWAYS use 2/3 Byzantine fault tolerance — no exceptions
        requiredMintSignatures = _max(MIN_REQUIRED_SIGNATURES, (validatorCount * 2 + 2) / 3);
    }

    /// @dev Both groups: 2/3 of the validators and a majority of at least 3 casters. Without 3 casters nothing is approved
    ///      here (only the caster bootstrap in updateCasters).
    function _requireApproval(bytes32 digest, bytes[] calldata signatures) internal view {
        require(_casters.length >= MIN_CASTERS, CastersNotSetUp());
        uint256 needCasters = requiredCasterSignatures();
        (uint256 v, uint256 c) = _countSigners(digest, signatures, requiredMintSignatures, needCasters);
        require(v >= requiredMintSignatures && c >= needCasters, InsufficientSignatures());
    }

    /**
     * @dev Distinct current validators and distinct current casters that signed `digest` (an address on both lists counts
     *      on both); malformed signatures and other signers are skipped. Stops once both counts reach what is asked.
     *      Counts nothing while the contract holds fewer than MIN_VALIDATORS validators (the implementation itself holds
     *      none), so no threshold computed from an empty set is ever met.
     */
    function _countSigners(bytes32 digest, bytes[] calldata signatures, uint256 needValidators, uint256 needCasters)
        internal view returns (uint256 v, uint256 c)
    {
        if (validatorCount < MIN_VALIDATORS) return (0, 0);
        address[] memory seen = new address[](signatures.length);
        uint256 n = 0;
        for (uint256 i = 0; i < signatures.length && (v < needValidators || c < needCasters); i++) {
            (address recovered, ECDSA.RecoverError error, ) = ECDSA.tryRecover(digest, signatures[i]);
            if (error != ECDSA.RecoverError.NoError) continue;
            bool asValidator = isValidator[recovered];
            bool asCaster = isCaster[recovered];
            if (!asValidator && !asCaster) continue;

            bool isDuplicate = false;
            for (uint256 j = 0; j < n; j++) {
                if (seen[j] == recovered) {
                    isDuplicate = true;
                    break;
                }
            }
            if (isDuplicate) continue;
            seen[n++] = recovered;
            if (asValidator) v++;
            if (asCaster) c++;
        }
    }

    function _lapsed(uint256 eta) internal view returns (bool) {
        return block.timestamp > eta + PROPOSAL_GRACE_PERIOD;
    }

    /// @dev The VFX address version byte of the VFX network this deployment bridges: 0x3C ('R') for the proxy on Base
    ///      mainnet, 0x89 ('x', testnet and devnet) on every other chain.
    function _vfxAddressVersion() internal view returns (uint256) {
        return _chainId() == 8453 ? 0x3C : 0x89;
    }

    /// @dev The chain this deployment runs on; a test harness answers for another chain.
    function _chainId() internal view virtual returns (uint256) {
        return block.chainid;
    }

    /// @dev A transparent VFX address as VFX nodes accept one (AddressValidateUtility.ValidateRBXAddress, written
    ///      canonically): Base58Check of 25 bytes, a version byte (this network's, or 0x89 for a reserve address, which
    ///      begins "xRBX" on every network), a 20-byte hash and the first four bytes of the double SHA-256 of the 21
    ///      before them. No spaces, names, shielded addresses or other chains' addresses: a burn to anything else was
    ///      never credited on VFX and nothing gives it back.
    function _isVfxAddress(string calldata addr) internal view returns (bool) {
        bytes calldata s = bytes(addr);
        // A leading '1' encodes a zero byte; the version byte is never zero, so a canonical address never starts with one.
        if (s.length < 26 || s.length > 35 || s[0] == "1") return false;
        uint256 value = 0;
        for (uint256 i = 0; i < s.length; i++) {
            uint256 digit = _base58Digit(s[i]);
            if (digit >= 58) return false;
            value = value * 58 + digit; // 58^35 < 2^206: no overflow
        }
        if (value >> 200 != 0) return false; // more than 25 bytes
        uint256 version = value >> 192;
        bool reserve = version == 0x89 && s[0] == "x" && s[1] == "R" && s[2] == "B" && s[3] == "X";
        if (version != _vfxAddressVersion() && !reserve) return false;
        bytes21 payload = bytes21(uint168(value >> 32));
        bytes32 check = sha256(abi.encodePacked(sha256(abi.encodePacked(payload))));
        return uint32(value) == uint32(bytes4(check));
    }

    /// @dev A Base58 character's value (Bitcoin's alphabet, no 0, O, I or l), or 58 for any other byte.
    function _base58Digit(bytes1 c) internal pure returns (uint256) {
        uint8 b = uint8(c);
        if (b >= 0x31 && b <= 0x39) return b - 0x31;      // 1-9  -> 0-8
        if (b >= 0x41 && b <= 0x48) return b - 0x41 + 9;  // A-H  -> 9-16
        if (b >= 0x4A && b <= 0x4E) return b - 0x4A + 17; // J-N  -> 17-21
        if (b >= 0x50 && b <= 0x5A) return b - 0x50 + 22; // P-Z  -> 22-32
        if (b >= 0x61 && b <= 0x6B) return b - 0x61 + 33; // a-k  -> 33-43
        if (b >= 0x6D && b <= 0x7A) return b - 0x6D + 44; // m-z  -> 44-57
        return 58;
    }

    function _max(uint256 a, uint256 b) internal pure returns (uint256) {
        return a >= b ? a : b;
    }

    /**
     * @dev Reserved storage slots for future upgrades. V4 took ten slots (its address shares V3's last slot), V5 five.
     */
    uint256[35] private __gap;

    function _authorizeUpgrade(address newImplementation) internal override {
        newImplementation; // silence
        require(_upgradeAuthorized, UseProposeUpgrade());
        _upgradeAuthorized = false;
    }
}
