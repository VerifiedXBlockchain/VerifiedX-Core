// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC20/ERC20.sol";
import "@openzeppelin/contracts/access/Ownable.sol";

/**
 * @title VBTCb - vBTC on Base
 * @notice Minimal ERC-20 token for vBTC.b on Base Sepolia (demo).
 *         The owner (relay node) can mint tokens when vBTC is bridge-locked on VerifiedX.
 *         Uses 8 decimals to match Bitcoin's satoshi denomination.
 *
 * Deploy with: forge create --rpc-url https://sepolia.base.org --private-key $PRIVATE_KEY VBTCb
 * Or via Remix: https://remix.ethereum.org
 */
contract VBTCb is ERC20, Ownable {
    /// @notice Emitted when a user burns vBTC.b to signal return to VerifiedX (demo bridge-back).
    /// @param burner Address whose tokens were burned (msg.sender)
    /// @param amount Amount burned (smallest units, 8 decimals)
    /// @param vfxLockId VerifiedX bridge lock id (same string returned by BridgeToBase API)
    event ExitBurned(address indexed burner, uint256 amount, string vfxLockId);

    constructor() ERC20("vBTC on Base", "vBTC.b") Ownable(msg.sender) {}

    function decimals() public pure override returns (uint8) {
        return 8;
    }

    /**
     * @notice Mint vBTC.b tokens. Only callable by the owner (relay node).
     * @param to Recipient address
     * @param amount Amount in satoshis (8 decimal places)
     */
    function mint(address to, uint256 amount) external onlyOwner {
        _mint(to, amount);
    }

    /**
     * @notice Burn vBTC.b tokens (for redemption back to BTC).
     * @param amount Amount in satoshis to burn
     */
    function burn(uint256 amount) external {
        _burn(msg.sender, amount);
    }

    /**
     * @notice Burn vBTC.b and tag the VerifiedX bridge lock to unlock (demo / testnet Flow C).
     *         The VFX node watches for ExitBurned and sets the matching lock to Unlocked.
     * @param amount Must equal the locked/minted amount for that lock (full exit only in this demo).
     * @param vfxLockId LockId from VerifiedX BridgeToBase response (case-sensitive string match on VFX).
     */
    function burnForExit(uint256 amount, string calldata vfxLockId) external {
        _burn(msg.sender, amount);
        emit ExitBurned(msg.sender, amount, vfxLockId);
    }
}