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
 * - cancelPending lets a majority of validators drop a pending upgrade or delay change.
 * - The validator set stays between 3 and 50; only batch add/remove remain.
 * - A malformed or unknown signature is skipped instead of reverting the call.
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
    uint256 public constant DEFAULT_UPGRADE_DELAY = 48 hours;
    uint256 public constant MAX_UPGRADE_DELAY = 30 days;
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
    event PendingCancelled(address implementation, uint256 upgradeDelay, uint256 adminNonce);

    bool private _upgradeAuthorized;

    // V4 storage: appended after V3's last variable and taken out of __gap.
    address public pendingImplementation;
    uint256 public pendingUpgradeEta;
    uint256 private _upgradeDelay;
    uint256 public pendingUpgradeDelay;
    uint256 public pendingUpgradeDelayEta;

    /// @custom:oz-upgrades-unsafe-allow constructor
    constructor() {
        _disableInitializers();
    }

    function initialize(
        string memory name_,
        string memory symbol_,
        address[] memory initialValidators
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
        _requireSignatures(digest, signatures, requiredMintSignatures, "Insufficient valid signatures");

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

    function addValidatorBatch(address[] calldata newValidators, uint256 vfxBlockHeight, bytes[] calldata signatures) external {
        require(newValidators.length > 0, "Empty batch");
        require(validatorCount + newValidators.length <= MAX_VALIDATORS, "Too many validators");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(
            ADD_VALIDATORS_TYPEHASH, keccak256(abi.encodePacked(newValidators)), vfxBlockHeight, adminNonce)));
        _requireSignatures(digest, signatures, requiredMintSignatures, "Insufficient valid admin signatures");

        for (uint256 i = 0; i < newValidators.length; i++) {
            require(newValidators[i] != address(0), "Invalid address");
            require(!isValidator[newValidators[i]], "Already a validator"); // also rejects a repeat within the batch

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
        _requireSignatures(digest, signatures, requiredRemoveSignatures, "Insufficient valid admin signatures");

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
        adminNonce++;
        _recalculateThresholds();

        emit ValidatorBatchRemoved(oldValidators, vfxBlockHeight);
    }

    /// @notice How long a proposed upgrade or delay change waits before it can be executed.
    function upgradeDelay() public view returns (uint256) {
        return _upgradeDelay == 0 ? DEFAULT_UPGRADE_DELAY : _upgradeDelay;
    }

    function proposeUpgrade(address newImplementation, bytes[] calldata signatures) external {
        require(newImplementation.code.length > 0, "Invalid implementation");
        require(pendingImplementation == address(0), "Upgrade already pending");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(PROPOSE_UPGRADE_TYPEHASH, newImplementation, adminNonce)));
        _requireSignatures(digest, signatures, requiredMintSignatures, "Insufficient valid admin signatures");

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

        pendingImplementation = address(0);
        pendingUpgradeEta = 0;

        _upgradeAuthorized = true;
        upgradeToAndCall(impl, new bytes(0));
        emit ContractUpgraded(impl, adminNonce);
    }

    function proposeUpgradeDelay(uint256 newDelay, bytes[] calldata signatures) external {
        require(newDelay >= DEFAULT_UPGRADE_DELAY && newDelay <= MAX_UPGRADE_DELAY, "Delay out of range");
        require(pendingUpgradeDelayEta == 0, "Delay change already pending");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(PROPOSE_UPGRADE_DELAY_TYPEHASH, newDelay, adminNonce)));
        _requireSignatures(digest, signatures, requiredMintSignatures, "Insufficient valid admin signatures");

        pendingUpgradeDelay = newDelay;
        pendingUpgradeDelayEta = block.timestamp + upgradeDelay();

        emit UpgradeDelayProposed(newDelay, pendingUpgradeDelayEta, adminNonce);
        adminNonce++;
    }

    function executeUpgradeDelay() external {
        require(pendingUpgradeDelayEta != 0, "No delay change pending");
        require(block.timestamp >= pendingUpgradeDelayEta, "Upgrade delay not elapsed");

        _upgradeDelay = pendingUpgradeDelay;
        pendingUpgradeDelay = 0;
        pendingUpgradeDelayEta = 0;

        emit UpgradeDelayChanged(_upgradeDelay);
    }

    /// @notice A majority of validators (the removal threshold) drops any pending upgrade and delay change.
    function cancelPending(bytes[] calldata signatures) external {
        require(pendingImplementation != address(0) || pendingUpgradeDelayEta != 0, "Nothing pending");

        bytes32 digest = _hashTypedData(keccak256(abi.encode(CANCEL_PENDING_TYPEHASH, adminNonce)));
        _requireSignatures(digest, signatures, requiredRemoveSignatures, "Insufficient valid admin signatures");

        emit PendingCancelled(pendingImplementation, pendingUpgradeDelay, adminNonce);

        pendingImplementation = address(0);
        pendingUpgradeEta = 0;
        pendingUpgradeDelay = 0;
        pendingUpgradeDelayEta = 0;
        adminNonce++;
    }

    function getValidators() external view returns (address[] memory) {
        return validators;
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

        if (validatorCount <= MIN_VALIDATORS_FOR_HIGH_THRESHOLD) {
            // Low validator count: use 51% for removals only (prevents lockout)
            requiredRemoveSignatures = _max(MIN_REQUIRED_SIGNATURES, (validatorCount + 1) / 2);
        } else {
            requiredRemoveSignatures = (validatorCount * 51 + 99) / 100;
        }
    }

    /// @dev Counts distinct current validators that signed `digest`; malformed signatures and non-validators are skipped.
    function _requireSignatures(bytes32 digest, bytes[] calldata signatures, uint256 required, string memory err) internal view {
        uint256 validSigCount = 0;
        address[] memory signers = new address[](signatures.length);

        for (uint256 i = 0; i < signatures.length && validSigCount < required; i++) {
            (address recovered, ECDSA.RecoverError error, ) = ECDSA.tryRecover(digest, signatures[i]);
            if (error != ECDSA.RecoverError.NoError || !isValidator[recovered]) continue;

            bool isDuplicate = false;
            for (uint256 j = 0; j < validSigCount; j++) {
                if (signers[j] == recovered) {
                    isDuplicate = true;
                    break;
                }
            }
            if (isDuplicate) continue;

            signers[validSigCount] = recovered;
            validSigCount++;
        }

        require(validSigCount >= required, err);
    }

    function _max(uint256 a, uint256 b) internal pure returns (uint256) {
        return a >= b ? a : b;
    }

    /**
     * @dev Reserved storage slots for future upgrades. V4 took four slots (its address shares V3's last slot).
     */
    uint256[46] private __gap;

    function _authorizeUpgrade(address newImplementation) internal override {
        newImplementation; // silence
        require(_upgradeAuthorized, "vBTCb: use proposeUpgrade and executeUpgrade");
        _upgradeAuthorized = false;
    }
}
