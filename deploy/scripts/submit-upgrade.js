const { ethers, upgrades, network } = require("hardhat");
const fs = require("fs");
const { requireProxy, contractAt, messageVersion, requestPath, validSigners, requiredCasterSignatures } = require("./upgrade-common");

/**
 * Step 2 of an upgrade. Reads upgrade-request.<network>.json, checks the signatures locally (distinct current
 * validators, enough of them, admin nonce unchanged) and only then sends:
 *   V3 proxy: upgradeWithValidatorApproval  -> the upgrade happens in this transaction
 *   V4 proxy: proposeUpgrade                -> starts upgradeDelay(); run execute-upgrade.js afterwards
 *
 * Usage (Windows cmd):
 *   set DEPLOYER_PRIVATE_KEY=0x...   any funded account (pays gas, gets no rights)
 *   set PROXY_ADDRESS=0x...
 *   set SIGNATURES=0xsig1,0xsig2
 *   npx hardhat run scripts/submit-upgrade.js --network baseMainnet
 */
async function main() {
    const proxy = requireProxy();
    const request = JSON.parse(fs.readFileSync(requestPath(), "utf8"));
    if (ethers.getAddress(request.proxy) !== proxy) throw new Error(`Request is for proxy ${request.proxy}, not ${proxy}`);

    const [sender] = await ethers.getSigners();
    const contract = await contractAt(proxy, sender);
    const version = await messageVersion(contract);
    if (version !== request.messageVersion) throw new Error(`Proxy is now message version ${version}; prepare again`);
    const adminNonce = await contract.adminNonce();
    if (adminNonce.toString() !== request.adminNonce)
        throw new Error(`Admin nonce moved from ${request.adminNonce} to ${adminNonce}; prepare again and re-sign`);

    const signatures = (process.env.SIGNATURES || "").split(",").map((s) => s.trim()).filter((s) => s.length > 0);
    const { validators: signers, casters, signatures: good } = await validSigners(contract, request, signatures);
    const required = await contract.requiredMintSignatures();
    const requiredCasters = await requiredCasterSignatures(contract);
    console.log(`Valid validator signatures: ${signers.length} of ${required} required`);
    signers.forEach((s) => console.log("  " + s));
    console.log(`Valid caster signatures: ${casters.length} of ${requiredCasters} required`);
    casters.forEach((s) => console.log("  " + s));
    if (BigInt(signers.length) < required || BigInt(casters.length) < requiredCasters) {
        console.error("ERROR: not enough valid signatures; nothing sent.");
        process.exit(1);
    }

    if (version === 3) {
        console.log(`\nUpgrading ${proxy} to ${request.implementation} (V3 path, immediate)...`);
        const tx = await contract.upgradeWithValidatorApproval(request.implementation, good);
        console.log("TX hash:", tx.hash);
        await tx.wait();
        console.log("Implementation now:", await upgrades.erc1967.getImplementationAddress(proxy));
    } else {
        console.log(`\nProposing upgrade of ${proxy} to ${request.implementation}...`);
        const tx = await contract.proposeUpgrade(request.implementation, good);
        console.log("TX hash:", tx.hash);
        await tx.wait();
        const eta = await contract.pendingUpgradeEta();
        console.log("Executable from:", new Date(Number(eta) * 1000).toISOString(), `(unix ${eta})`);
        console.log("Then run scripts/execute-upgrade.js (anyone may).");
    }
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });
