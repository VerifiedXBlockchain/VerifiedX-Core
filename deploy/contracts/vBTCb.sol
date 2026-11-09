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
 */
contract vBTCb is ERC20Upgradeable, UUPSUpgradeable {
    mapping(address => bool) public isValidator;
    address[] public validators;
    uint256 public validatorCount;
    uint256 public requiredMintSignatures;
    uint256 public requiredRemoveSignatures;

    uint256 public constant MIN_VALIDATORS_FOR_HIGH_THRESHOLD = 5;
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

    bytes32 private constant DOMAIN_TYPEHASH =
        keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");
    bytes32 private constant DOMAIN_NAME_HASH = keccak256("vBTCb");
    bytes32 private constant DOMAIN_VERSION_HASH = keccak256("4");

    bytes32 public constant MINT_TYPEHASH =
        keccak256("Mint(address to,uint256 amount,string lockId,uint256 nonce)");
    bytes32 public constant ADD_VALIDATORS_TYPEHASH =
        keccak256("AddValidators(address[] validators,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 public constant REMOVE_VALIDATORS_TYPEHASH =
        keccak256("RemoveValidators(address[] validators,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 public constant PROPOSE_UPGRADE_TYPEHASH =
        keccak256("ProposeUpgrade(address implementation,uint256 adminNonce)");
    bytes32 public constant PROPOSE_UPGRADE_DELAY_TYPEHASH =
        keccak256("ProposeUpgradeDelay(uint256 delay,uint256 adminNonce)");
    bytes32 public constant PROPOSE_REMOVAL_TYPEHASH =
        keccak256("ProposeRemoval(address[] validators,address[] casters,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 public constant UPDATE_CASTERS_TYPEHASH =
        keccak256("UpdateCasters(address[] add,address[] remove,uint256 vfxBlockHeight,uint256 adminNonce)");
    bytes32 public constant CANCEL_PENDING_TYPEHASH =
        keccak256("CancelPending(uint256 adminNonce)");

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

        require(initialValidators.length >= MIN_VALIDATORS, "Need at least 3 validators");
        require(initialValidators.length <= MAX_VALIDATORS, "Too many validators");

        for (uint256 i = 0; i < initialValidators.length; i++) {
            require(initialValidators[i] != address(0), "Invalid validator address");
            require(!isValidator[initialValidators[i]], "Duplicate validator");
            isValidator[initialValidators[i]] = true;
            validators.push(initialValidators[i]);
        }

        validatorCount = initialValidators.length;
        _recalculateThresholds();

        require(initialCasters.length >= MIN_CASTERS && initialCasters.length <= MAX_CASTERS, "Caster count out of range");
        for (uint256 i = 0; i < initialCasters.length; i++) {
            require(initialCasters[i] != address(0), "Invalid caster address");
            require(!isCaster[initialCasters[i]], "Duplicate caster");
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
    ) external {
        require(to != address(0), "Invalid recipient");
        require(amount > 0, "Amount must be > 0");

        bytes32 lockIdHash = keccak256(bytes(lockId));
        require(!usedLockIds[lockIdHash], "LockId already used");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(MINT_TYPEHASH, to, amount, lockIdHash, nonce)));
        _requireApproval(digest, signatures, "Insufficient valid signatures");

        usedLockIds[lockIdHash] = true;
        _mint(to, amount);

        emit MintExecuted(to, amount, lockId, nonce);
    }

    /**
     * @notice Burn vBTC.b tokens to exit back to VFX chain.
     * @dev Pool-based unlock: burner specifies a VFX destination address.
     *      The VFX network will select available bridge locks FIFO to fulfill the exit,
     *      crediting vBTC from one or more contracts to the destination address.
     * @param amount Amount of vBTC.b to burn (8 decimals, in sats)
     * @param vfxDestinationAddress The VFX address to receive the unlocked vBTC
     */
    function burnForVfxExit(uint256 amount, string calldata vfxDestinationAddress) external {
        require(amount > 0, "Amount must be > 0");
        require(bytes(vfxDestinationAddress).length > 0, "VFX destination required");
        _burn(msg.sender, amount);
        emit VfxExitBurned(msg.sender, amount, vfxDestinationAddress, block.chainid);
    }

    /**
     * @notice Burn vBTC.b tokens to exit directly to a BTC address.
     * @dev The VFX network FROST-signs a BTC transaction to the specified destination.
     * @param amount Amount of vBTC.b to burn (8 decimals, in sats)
     * @param btcDestination The Bitcoin address to send BTC to
     */
    function burnForBTCExit(uint256 amount, string calldata btcDestination) external {
        require(amount > 0, "Amount must be > 0");
        require(bytes(btcDestination).length >= 26, "Invalid BTC address");
        _burn(msg.sender, amount);
        emit BTCExitBurned(msg.sender, amount, btcDestination, block.chainid);
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
    ) external {
        require(newValidators.length > 0, "Empty batch");
        require(validatorCount + newValidators.length <= MAX_VALIDATORS, "Too many validators");
        require(newValidatorSignatures.length == newValidators.length, "One signature per new validator");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            ADD_VALIDATORS_TYPEHASH, keccak256(abi.encodePacked(newValidators)), vfxBlockHeight, adminNonce)));
        _requireApproval(digest, signatures, "Insufficient valid admin signatures");

        for (uint256 i = 0; i < newValidators.length; i++) {
            require(newValidators[i] != address(0), "Invalid address");
            require(!isValidator[newValidators[i]], "Already a validator"); // also rejects a repeat within the batch
            (address own, ECDSA.RecoverError error, ) = ECDSA.tryRecover(digest, newValidatorSignatures[i]);
            require(error == ECDSA.RecoverError.NoError && own == newValidators[i], "New validator did not sign");

            isValidator[newValidators[i]] = true;
            validators.push(newValidators[i]);
        }

        validatorCount += newValidators.length;
        adminNonce++;
        _recalculateThresholds();

        emit ValidatorBatchAdded(newValidators, vfxBlockHeight);
    }

    function removeValidatorBatch(address[] calldata oldValidators, uint256 vfxBlockHeight, bytes[] calldata signatures) external {
        require(oldValidators.length > 0, "Empty batch");
        require(oldValidators.length <= validatorCount && validatorCount - oldValidators.length >= MIN_VALIDATORS,
            "Cannot remove below minimum");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            REMOVE_VALIDATORS_TYPEHASH, keccak256(abi.encodePacked(oldValidators)), vfxBlockHeight, adminNonce)));
        // The mint approval: with less, a group could remove the rest and be 2/3 of the set that is left.
        _requireApproval(digest, signatures, "Insufficient valid admin signatures");

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
    ) external {
        require(oldValidators.length + oldCasters.length > 0, "Empty batch");
        require(oldValidators.length <= validatorCount && validatorCount - oldValidators.length >= MIN_VALIDATORS,
            "Cannot remove below minimum");
        require(oldCasters.length == 0 || (oldCasters.length <= _casters.length && _casters.length - oldCasters.length >= MIN_CASTERS),
            "Cannot remove casters below minimum");
        require(pendingRemovalEta == 0 || _lapsed(pendingRemovalEta), "Removal already pending");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            PROPOSE_REMOVAL_TYPEHASH, keccak256(abi.encodePacked(oldValidators)), keccak256(abi.encodePacked(oldCasters)),
            vfxBlockHeight, adminNonce)));
        (uint256 v, uint256 c) = _countSigners(digest, signatures, requiredMintSignatures, requiredCasterSignatures());
        require((v >= requiredRemoveSignatures && c >= requiredCasterSignatures()) || v >= requiredMintSignatures,
            "Insufficient valid admin signatures");

        for (uint256 i = 0; i < oldValidators.length; i++) {
            require(isValidator[oldValidators[i]], "Not a validator");
            for (uint256 j = 0; j < i; j++) require(oldValidators[j] != oldValidators[i], "Duplicate in batch");
        }
        for (uint256 i = 0; i < oldCasters.length; i++) {
            require(isCaster[oldCasters[i]], "Not a caster");
            for (uint256 j = 0; j < i; j++) require(oldCasters[j] != oldCasters[i], "Duplicate in batch");
        }

        _pendingRemovals = oldValidators;
        _pendingCasterRemovals = oldCasters;
        pendingRemovalHeight = vfxBlockHeight;
        pendingRemovalEta = block.timestamp + upgradeDelay();

        emit RemovalProposed(oldValidators, oldCasters, vfxBlockHeight, pendingRemovalEta, adminNonce);
        adminNonce++;
    }

    /// @notice Anyone may carry out a proposed removal once its delay has passed.
    function executeRemoval() external {
        require(pendingRemovalEta != 0, "No removal pending");
        require(block.timestamp >= pendingRemovalEta, "Upgrade delay not elapsed");
        require(!_lapsed(pendingRemovalEta), "Proposal lapsed");

        // Skip any already removed since the proposal (by the 2/3 path).
        uint256 n = 0;
        for (uint256 i = 0; i < _pendingRemovals.length; i++) {
            if (isValidator[_pendingRemovals[i]]) n++;
        }
        address[] memory list = new address[](n);
        uint256 k = 0;
        for (uint256 i = 0; i < _pendingRemovals.length; i++) {
            if (isValidator[_pendingRemovals[i]]) list[k++] = _pendingRemovals[i];
        }
        // Casters likewise, skipping any already gone.
        uint256 m = 0;
        for (uint256 i = 0; i < _pendingCasterRemovals.length; i++) {
            if (isCaster[_pendingCasterRemovals[i]]) m++;
        }
        address[] memory casterList = new address[](m);
        k = 0;
        for (uint256 i = 0; i < _pendingCasterRemovals.length; i++) {
            if (isCaster[_pendingCasterRemovals[i]]) casterList[k++] = _pendingCasterRemovals[i];
        }
        uint256 height = pendingRemovalHeight;
        delete _pendingRemovals;
        delete _pendingCasterRemovals;
        pendingRemovalHeight = 0;
        pendingRemovalEta = 0;

        require(validatorCount - n >= MIN_VALIDATORS, "Cannot remove below minimum");
        require(m == 0 || _casters.length - m >= MIN_CASTERS, "Cannot remove casters below minimum");
        if (n > 0) _removeValidators(list, height);
        if (m > 0) {
            for (uint256 i = 0; i < m; i++) _removeCaster(casterList[i]);
            emit CastersUpdated(new address[](0), casterList, height);
        }
    }

    function getPendingRemovals() external view returns (address[] memory) {
        return _pendingRemovals;
    }

    function getPendingCasterRemovals() external view returns (address[] memory) {
        return _pendingCasterRemovals;
    }

    /**
     * @notice Brings the caster list in step with the VFX caster committee: removals, then additions, in one call.
     * @param newCasterSignatures one per added caster, in the same order: its own signature over the same UpdateCasters
     *        message, so no seat goes to an address whose key nobody holds.
     */
    function updateCasters(
        address[] calldata add,
        address[] calldata remove,
        uint256 vfxBlockHeight,
        bytes[] calldata signatures,
        bytes[] calldata newCasterSignatures
    ) external {
        require(add.length + remove.length > 0, "Empty batch");
        require(newCasterSignatures.length == add.length, "One signature per new caster");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            UPDATE_CASTERS_TYPEHASH, keccak256(abi.encodePacked(add)), keccak256(abi.encodePacked(remove)), vfxBlockHeight, adminNonce)));
        _requireApproval(digest, signatures, "Insufficient valid admin signatures");

        for (uint256 i = 0; i < remove.length; i++) {
            require(isCaster[remove[i]], "Not a caster"); // also rejects a repeat within the batch
            _removeCaster(remove[i]);
        }
        for (uint256 i = 0; i < add.length; i++) {
            require(add[i] != address(0), "Invalid address");
            require(!isCaster[add[i]], "Already a caster"); // also rejects a repeat within the batch
            (address own, ECDSA.RecoverError error, ) = ECDSA.tryRecover(digest, newCasterSignatures[i]);
            require(error == ECDSA.RecoverError.NoError && own == add[i], "New caster did not sign");
            isCaster[add[i]] = true;
            _casters.push(add[i]);
        }
        require(_casters.length >= MIN_CASTERS && _casters.length <= MAX_CASTERS, "Caster count out of range");

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
            require(isValidator[oldValidators[i]], "Not a validator"); // also rejects a repeat within the batch

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

    function proposeUpgrade(address newImplementation, bytes[] calldata signatures) external {
        require(newImplementation.code.length > 0, "Invalid implementation");
        require(pendingImplementation == address(0) || _lapsed(pendingUpgradeEta), "Upgrade already pending");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(PROPOSE_UPGRADE_TYPEHASH, newImplementation, adminNonce)));
        _requireApproval(digest, signatures, "Insufficient valid admin signatures");

        pendingImplementation = newImplementation;
        pendingUpgradeEta = block.timestamp + upgradeDelay();

        emit UpgradeProposed(newImplementation, pendingUpgradeEta, adminNonce);
        adminNonce++;
    }

    /// @notice Anyone may execute a proposed upgrade once its delay has passed. No call data is passed to it.
    function executeUpgrade() external {
        address impl = pendingImplementation;
        require(impl != address(0), "No upgrade pending");
        require(block.timestamp >= pendingUpgradeEta, "Upgrade delay not elapsed");
        require(!_lapsed(pendingUpgradeEta), "Proposal lapsed");

        pendingImplementation = address(0);
        pendingUpgradeEta = 0;

        _upgradeAuthorized = true;
        upgradeToAndCall(impl, new bytes(0));
        emit ContractUpgraded(impl, adminNonce);
    }

    function proposeUpgradeDelay(uint256 newDelay, bytes[] calldata signatures) external {
        require(newDelay >= DEFAULT_UPGRADE_DELAY && newDelay <= MAX_UPGRADE_DELAY, "Delay out of range");
        require(pendingUpgradeDelayEta == 0 || _lapsed(pendingUpgradeDelayEta), "Delay change already pending");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(PROPOSE_UPGRADE_DELAY_TYPEHASH, newDelay, adminNonce)));
        _requireApproval(digest, signatures, "Insufficient valid admin signatures");

        pendingUpgradeDelay = newDelay;
        pendingUpgradeDelayEta = block.timestamp + upgradeDelay();

        emit UpgradeDelayProposed(newDelay, pendingUpgradeDelayEta, adminNonce);
        adminNonce++;
    }

    function executeUpgradeDelay() external {
        require(pendingUpgradeDelayEta != 0, "No delay change pending");
        require(block.timestamp >= pendingUpgradeDelayEta, "Upgrade delay not elapsed");
        require(!_lapsed(pendingUpgradeDelayEta), "Proposal lapsed");

        _upgradeDelay = pendingUpgradeDelay;
        pendingUpgradeDelay = 0;
        pendingUpgradeDelayEta = 0;

        emit UpgradeDelayChanged(_upgradeDelay);
    }

    /// @notice The validators who could refuse a mint: validatorCount - requiredMintSignatures + 1.
    function requiredVetoSignatures() public view returns (uint256) {
        return validatorCount - requiredMintSignatures + 1;
    }

    /// @notice A majority of the casters (0 while the contract has none, after an upgrade from V3).
    function requiredCasterSignatures() public view returns (uint256) {
        return _casters.length == 0 ? 0 : _casters.length / 2 + 1;
    }

    /// @notice The casters who could refuse a mint: casterCount - requiredCasterSignatures + 1 (0: no casters, no caster veto).
    function requiredCasterVetoSignatures() public view returns (uint256) {
        return _casters.length == 0 ? 0 : _casters.length - requiredCasterSignatures() + 1;
    }

    /// @notice Either group's blocking minority drops whatever is pending: an upgrade, a delay change and a removal.
    function cancelPending(bytes[] calldata signatures) external {
        require(pendingImplementation != address(0) || pendingUpgradeDelayEta != 0 || pendingRemovalEta != 0, "Nothing pending");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(CANCEL_PENDING_TYPEHASH, adminNonce)));
        uint256 validatorVeto = requiredVetoSignatures();
        uint256 casterVeto = requiredCasterVetoSignatures();
        (uint256 v, uint256 c) = _countSigners(digest, signatures, validatorVeto, casterVeto == 0 ? type(uint256).max : casterVeto);
        require(v >= validatorVeto || (casterVeto > 0 && c >= casterVeto), "Insufficient valid admin signatures");

        emit PendingCancelled(pendingImplementation, pendingUpgradeDelay, _pendingRemovals.length + _pendingCasterRemovals.length, adminNonce);

        pendingImplementation = address(0);
        pendingUpgradeEta = 0;
        pendingUpgradeDelay = 0;
        pendingUpgradeDelayEta = 0;
        delete _pendingRemovals;
        delete _pendingCasterRemovals;
        pendingRemovalHeight = 0;
        pendingRemovalEta = 0;
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

        // A strict majority, for proposeRemoval only (the timelocked way out when too many validators went dark for
        // 2/3 to be reached). MIN_VALIDATORS_FOR_HIGH_THRESHOLD is V3's and no longer used.
        requiredRemoveSignatures = _max(MIN_REQUIRED_SIGNATURES, validatorCount / 2 + 1);
    }

    /// @dev Both groups: 2/3 of the validators and a majority of the casters.
    function _requireApproval(bytes32 digest, bytes[] calldata signatures, string memory err) internal view {
        uint256 needCasters = requiredCasterSignatures();
        (uint256 v, uint256 c) = _countSigners(digest, signatures, requiredMintSignatures, needCasters);
        require(v >= requiredMintSignatures && c >= needCasters, err);
    }

    /**
     * @dev Distinct current validators and distinct current casters that signed `digest` (an address on both lists counts
     *      on both); malformed signatures and other signers are skipped. Stops once both counts reach what is asked.
     */
    function _countSigners(bytes32 digest, bytes[] calldata signatures, uint256 needValidators, uint256 needCasters)
        internal view returns (uint256 v, uint256 c)
    {
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

    function _max(uint256 a, uint256 b) internal pure returns (uint256) {
        return a >= b ? a : b;
    }

    /**
     * @dev Reserved storage slots for future upgrades. V4 took ten slots (its address shares V3's last slot).
     */
    uint256[40] private __gap;

    function _authorizeUpgrade(address newImplementation) internal override {
        newImplementation; // silence
        require(_upgradeAuthorized, "vBTCb: use proposeUpgrade and executeUpgrade");
        _upgradeAuthorized = false;
    }
}
