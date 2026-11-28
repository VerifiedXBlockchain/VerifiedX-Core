// SPDX-License-Identifier: MIT
pragma solidity ^0.8.26;

import "../vBTCb.sol";

/// @dev Tests only, never deployed: the VFX address check as the contract runs it on a chain the test names, so the
///      Base mainnet rules (chain 8453) are exercised on Hardhat's own chain.
contract VfxAddressHarness is vBTCb {
    uint256 private _testChainId;

    function setChainId(uint256 chainId) external {
        _testChainId = chainId;
    }

    function isVfxAddress(string calldata addr) external view returns (bool) {
        return _isVfxAddress(addr);
    }

    function _chainId() internal view override returns (uint256) {
        return _testChainId == 0 ? block.chainid : _testChainId;
    }
}
