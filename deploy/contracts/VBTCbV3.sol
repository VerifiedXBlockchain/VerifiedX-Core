// SPDX-License-Identifier: MIT
pragma solidity ^0.8.26;

import "@openzeppelin/contracts-upgradeable/token/ERC20/ERC20Upgradeable.sol";
import "@openzeppelin/contracts-upgradeable/proxy/utils/UUPSUpgradeable.sol";
import "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import "@openzeppelin/contracts/utils/cryptography/MessageHashUtils.sol";

/**
 * @title VBTCbV3 - Verified Bitcoin on Base (Production)
 * @notice Multi-sig verified ERC-20 for vBTC bridged from VFX. No owner; upgrades via validator multi-sig.
 * @dev Deploy behind an ERC1967 UUPS proxy. Initializer seeds the validator registry.
 *
 * V3 Changes:
 * - Replaced burnForExit(amount, lockId) with burnForVfxExit(amount, vfxDestinationAddress)
 *   to support pool-based unlocks (fungible exit — any holder can exit, not just original locker)
 * - Added VfxExitBurned event with vfxDestinationAddress instead of lockId
 */
contract VBTCbV3 is ERC20Upgradeable, UUPSUpgradeable {
    using ECDSA for bytes32;
    using MessageHashUtils for bytes32;

    mapping(address => bool) public isValidator;
    address[] public validators;
    uint256 public validatorCount;
    uint256 public requiredMintSignatures;
    uint256 public requiredRemoveSignatures;

    uint256 public constant MIN_VALIDATORS_FOR_HIGH_THRESHOLD = 5;
    uint256 public constant MIN_REQUIRED_SIGNATURES = 2;

    mapping(bytes32 => bool) public usedLockIds;
    uint256 public adminNonce;

    event MintExecuted(address indexed to, uint256 amount, string lockId, uint256 nonce);
    event VfxExitBurned(address indexed burner, uint256 amount, string vfxDestinationAddress, uint256 chainId);
    event BTCExitBurned(address indexed burner, uint256 amount, string btcDestination, uint256 chainId);
    event ValidatorAdded(address indexed validator, uint256 vfxBlockHeight);
    event ValidatorRemoved(address indexed validator, uint256 vfxBlockHeight);
    event ValidatorBatchAdded(address[] validators, uint256 vfxBlockHeight);
    event ValidatorBatchRemoved(address[] validators, uint256 vfxBlockHeight);
    event ContractUpgraded(address indexed newImplementation, uint256 adminNonce);

    bool private _upgradeAuthorized;

    function initialize(
        string memory name_,
        string memory symbol_,
        address[] memory initialValidators
    ) public initializer {
        __ERC20_init(name_, symbol_);

        require(initialValidators.length >= 2, "Need at least 2 validators");

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

        bytes32 lockIdHash = keccak256(abi.encodePacked(lockId));
        require(!usedLockIds[lockIdHash], "LockId already used");

        bytes32 messageHash = keccak256(abi.encodePacked(to, amount, lockId, nonce, block.chainid, address(this)));
        bytes32 ethSignedHash = messageHash.toEthSignedMessageHash();

        uint256 validSigCount = 0;
        address[] memory signers = new address[](signatures.length);

        for (uint256 i = 0; i < signatures.length; i++) {
            address recovered = ethSignedHash.recover(signatures[i]);
            if (!isValidator[recovered]) continue;

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

        require(validSigCount >= requiredMintSignatures, "Insufficient valid signatures");

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

    function addValidator(address newValidator, uint256 vfxBlockHeight, bytes[] calldata signatures) external {
        require(newValidator != address(0), "Invalid address");
        require(!isValidator[newValidator], "Already a validator");

        bytes32 messageHash = keccak256(
            abi.encodePacked("ADD", newValidator, vfxBlockHeight, adminNonce, block.chainid, address(this))
        );
        bytes32 ethSignedHash = messageHash.toEthSignedMessageHash();

        _verifyAdminSignatures(ethSignedHash, signatures, requiredMintSignatures);

        isValidator[newValidator] = true;
        validators.push(newValidator);
        validatorCount++;
        adminNonce++;

        _recalculateThresholds();

        emit ValidatorAdded(newValidator, vfxBlockHeight);
    }

    function addValidatorBatch(address[] calldata newValidators, uint256 vfxBlockHeight, bytes[] calldata signatures) external {
        require(newValidators.length > 0, "Empty batch");
        require(newValidators.length <= 100, "Batch too large");

        for (uint256 i = 0; i < newValidators.length; i++) {
            for (uint256 j = i + 1; j < newValidators.length; j++) {
                require(newValidators[i] != newValidators[j], "Duplicate in batch");
            }
        }

        bytes32 messageHash = keccak256(
            abi.encodePacked("ADD_BATCH", abi.encodePacked(newValidators), vfxBlockHeight, adminNonce, block.chainid, address(this))
        );
        bytes32 ethSignedHash = messageHash.toEthSignedMessageHash();

        _verifyAdminSignatures(ethSignedHash, signatures, requiredMintSignatures);

        for (uint256 i = 0; i < newValidators.length; i++) {
            require(newValidators[i] != address(0), "Invalid address");
            require(!isValidator[newValidators[i]], "Already a validator");

            isValidator[newValidators[i]] = true;
            validators.push(newValidators[i]);
            validatorCount++;
        }

        adminNonce++;
        _recalculateThresholds();

        emit ValidatorBatchAdded(newValidators, vfxBlockHeight);
    }

    function removeValidator(address oldValidator, uint256 vfxBlockHeight, bytes[] calldata signatures) external {
        require(isValidator[oldValidator], "Not a validator");
        require(validatorCount > MIN_REQUIRED_SIGNATURES, "Cannot remove below minimum");

        bytes32 messageHash = keccak256(
            abi.encodePacked("REMOVE", oldValidator, vfxBlockHeight, adminNonce, block.chainid, address(this))
        );
        bytes32 ethSignedHash = messageHash.toEthSignedMessageHash();

        _verifyAdminSignatures(ethSignedHash, signatures, requiredRemoveSignatures);

        isValidator[oldValidator] = false;

        for (uint256 i = 0; i < validators.length; i++) {
            if (validators[i] == oldValidator) {
                validators[i] = validators[validators.length - 1];
                validators.pop();
                break;
            }
        }

        validatorCount--;
        adminNonce++;
        _recalculateThresholds();

        emit ValidatorRemoved(oldValidator, vfxBlockHeight);
    }

    function removeValidatorBatch(address[] calldata oldValidators, uint256 vfxBlockHeight, bytes[] calldata signatures) external {
        require(oldValidators.length > 0, "Empty batch");
        require(oldValidators.length <= 100, "Batch too large");
        require(validatorCount - oldValidators.length >= MIN_REQUIRED_SIGNATURES, "Cannot remove below minimum");

        for (uint256 i = 0; i < oldValidators.length; i++) {
            for (uint256 j = i + 1; j < oldValidators.length; j++) {
                require(oldValidators[i] != oldValidators[j], "Duplicate in batch");
            }
        }

        bytes32 messageHash = keccak256(
            abi.encodePacked("REMOVE_BATCH", abi.encodePacked(oldValidators), vfxBlockHeight, adminNonce, block.chainid, address(this))
        );
        bytes32 ethSignedHash = messageHash.toEthSignedMessageHash();

        _verifyAdminSignatures(ethSignedHash, signatures, requiredRemoveSignatures);

        for (uint256 i = 0; i < oldValidators.length; i++) {
            require(isValidator[oldValidators[i]], "Not a validator");

            isValidator[oldValidators[i]] = false;

            for (uint256 j = 0; j < validators.length; j++) {
                if (validators[j] == oldValidators[i]) {
                    validators[j] = validators[validators.length - 1];
                    validators.pop();
                    break;
                }
            }

            validatorCount--;
        }

        adminNonce++;
        _recalculateThresholds();

        emit ValidatorBatchRemoved(oldValidators, vfxBlockHeight);
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

    function _verifyAdminSignatures(bytes32 ethSignedHash, bytes[] calldata signatures, uint256 required) internal view {
        uint256 validSigCount = 0;
        address[] memory signers = new address[](signatures.length);

        for (uint256 i = 0; i < signatures.length; i++) {
            address recovered = ethSignedHash.recover(signatures[i]);
            if (!isValidator[recovered]) continue;

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

        require(validSigCount >= required, "Insufficient valid admin signatures");
    }

    function _max(uint256 a, uint256 b) internal pure returns (uint256) {
        return a >= b ? a : b;
    }

    /**
     * @dev Reserved storage slots for future upgrades.
     * This ensures that adding new state variables in future versions
     * does not shift the storage layout of derived contracts.
     */
    uint256[50] private __gap;

    function _authorizeUpgrade(address newImplementation) internal override {
        newImplementation; // silence
        require(_upgradeAuthorized, "VBTCbV3: use upgradeWithValidatorApproval");
        _upgradeAuthorized = false;
    }

    function upgradeWithValidatorApproval(address newImplementation, bytes[] calldata signatures) external {
        require(newImplementation != address(0), "Invalid implementation");

        bytes32 messageHash = keccak256(abi.encodePacked("UPGRADE", newImplementation, adminNonce, block.chainid, address(this)));
        bytes32 ethSignedHash = messageHash.toEthSignedMessageHash();

        _verifyAdminSignatures(ethSignedHash, signatures, requiredMintSignatures);

        adminNonce++;
        _upgradeAuthorized = true;
        upgradeToAndCall(newImplementation, new bytes(0));
        emit ContractUpgraded(newImplementation, adminNonce - 1);
    }
}
