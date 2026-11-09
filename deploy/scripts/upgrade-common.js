const { ethers, network } = require("hardhat");
const path = require("path");

// Members both V3 and V4 have, plus the V4-only ones read inside try/catch.
const ABI = [
    "function adminNonce() view returns (uint256)",
    "function getValidators() view returns (address[])",
    "function isValidator(address) view returns (bool)",
    "function requiredMintSignatures() view returns (uint256)",
    "function isCaster(address) view returns (bool)",
    "function requiredCasterSignatures() view returns (uint256)",
    "function MESSAGE_VERSION() view returns (uint256)",
    "function upgradeDelay() view returns (uint256)",
    "function pendingImplementation() view returns (address)",
    "function pendingUpgradeEta() view returns (uint256)",
    "function PROPOSAL_GRACE_PERIOD() view returns (uint256)",
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

/** Whether `signer` is on the contract's caster list (V3 has none). */
async function isCaster(contract, signer) {
    try {
        return await contract.isCaster(signer);
    } catch {
        return false;
    }
}

/** The casters' share V4 needs besides 2/3 of the validators (0 on V3 and on a V4 without casters yet). */
async function requiredCasterSignatures(contract) {
    try {
        return await contract.requiredCasterSignatures();
    } catch {
        return 0n;
    }
}

/**
 * The implementation of a V4 upgrade that is pending and can still be executed, or null. One whose grace period ran out
 * can no longer be executed and may be replaced by a new proposal.
 */
async function livePendingUpgrade(contract) {
    const pending = await contract.pendingImplementation();
    if (pending === ethers.ZeroAddress) return null;
    const eta = await contract.pendingUpgradeEta();
    const grace = await contract.PROPOSAL_GRACE_PERIOD();
    const now = BigInt((await ethers.provider.getBlock("latest")).timestamp);
    return now > eta + grace ? null : pending;
}

/**
 * Recovers each signature for the request and returns the distinct current validators and the distinct current casters
 * among them (an address on both lists is in both), and `signatures`: one per distinct signer that counts, malformed
 * ones and the rest left out. Send only those: V3 reverts the whole call on a single malformed signature.
 */
async function validSigners(contract, request, signatures) {
    const found = new Set();
    const casters = new Set();
    const good = [];
    const seen = new Set();
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
        const asValidator = await contract.isValidator(signer);
        const asCaster = await isCaster(contract, signer);
        if (asValidator) found.add(signer);
        if (asCaster) casters.add(signer);
        if (!asValidator && !asCaster) console.warn("  skipped signature from a non-validator, non-caster", signer);
        else if (!seen.has(signer)) {
            seen.add(signer);
            good.push(sig);
        }
    }
    return { validators: [...found], casters: [...casters], signatures: good };
}

module.exports = { ABI, PROPOSE_UPGRADE_TYPES, requireProxy, contractAt, messageVersion, requestPath, validSigners, requiredCasterSignatures, livePendingUpgrade };
