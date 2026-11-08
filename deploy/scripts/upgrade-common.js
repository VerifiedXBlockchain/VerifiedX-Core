const { ethers, network } = require("hardhat");
const path = require("path");

// Members both V3 and V4 have, plus the V4-only ones read inside try/catch.
const ABI = [
    "function adminNonce() view returns (uint256)",
    "function getValidators() view returns (address[])",
    "function isValidator(address) view returns (bool)",
    "function requiredMintSignatures() view returns (uint256)",
    "function MESSAGE_VERSION() view returns (uint256)",
    "function upgradeDelay() view returns (uint256)",
    "function pendingImplementation() view returns (address)",
    "function pendingUpgradeEta() view returns (uint256)",
    "function upgradeWithValidatorApproval(address newImplementation, bytes[] signatures)",
    "function proposeUpgrade(address newImplementation, bytes[] signatures)",
    "function executeUpgrade()",
];

const PROPOSE_UPGRADE_TYPES = {
    ProposeUpgrade: [
        { name: "implementation", type: "address" },
        { name: "adminNonce", type: "uint256" },
    ],
};

function requireProxy() {
    const proxy = process.env.PROXY_ADDRESS;
    if (!proxy) {
        console.error("ERROR: set PROXY_ADDRESS to the vBTCb proxy address.");
        process.exit(1);
    }
    return ethers.getAddress(proxy);
}

async function contractAt(proxy, runner) {
    return new ethers.Contract(proxy, ABI, runner ?? ethers.provider);
}

/** 3 for the deployed V3 (no MESSAGE_VERSION), otherwise the version the contract reports. */
async function messageVersion(contract) {
    try {
        return Number(await contract.MESSAGE_VERSION());
    } catch {
        return 3;
    }
}

function requestPath() {
    return path.join(__dirname, "..", `upgrade-request.${network.name}.json`);
}

/** Recovers each signature for the request and returns the distinct current validators among them. */
async function validSigners(contract, request, signatures) {
    const found = new Set();
    for (const sig of signatures) {
        let signer;
        try {
            signer =
                request.messageVersion === 3
                    ? ethers.verifyMessage(ethers.getBytes(request.hash), sig)
                    : ethers.verifyTypedData(request.typedData.domain, PROPOSE_UPGRADE_TYPES, request.typedData.value, sig);
        } catch {
            console.warn("  skipped malformed signature", sig.slice(0, 12) + "...");
            continue;
        }
        if (await contract.isValidator(signer)) found.add(signer);
        else console.warn("  skipped signature from non-validator", signer);
    }
    return [...found];
}

module.exports = { ABI, PROPOSE_UPGRADE_TYPES, requireProxy, contractAt, messageVersion, requestPath, validSigners };
