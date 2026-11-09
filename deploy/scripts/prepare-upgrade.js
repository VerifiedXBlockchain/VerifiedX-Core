const { ethers, upgrades, network } = require("hardhat");
const fs = require("fs");
const { PROPOSE_UPGRADE_TYPES, requireProxy, contractAt, messageVersion, requestPath, livePendingUpgrade } = require("./upgrade-common");

/**
 * Step 1 of an upgrade. Checks the new contracts/vBTCb.sol against the storage layout recorded in
 * deploy/.openzeppelin/<network>.json, deploys the new implementation (no rights, no state), and writes
 * upgrade-request.<network>.json with exactly what the validators must sign.
 *
 * Usage (Windows cmd):
 *   set DEPLOYER_PRIVATE_KEY=0x...   any funded account (pays for the implementation deployment)
 *   set PROXY_ADDRESS=0x...          the existing proxy
 *   npx hardhat run scripts/prepare-upgrade.js --network baseMainnet
 */
async function main() {
    const proxy = requireProxy();
    const { chainId } = await ethers.provider.getNetwork();
    const contract = await contractAt(proxy);
    const version = await messageVersion(contract);

    console.log(`=== Prepare vBTCb upgrade on ${network.name} (chain ${chainId}) ===`);
    console.log("Proxy:", proxy);
    console.log("Current implementation:", await upgrades.erc1967.getImplementationAddress(proxy));
    console.log("Current message version:", version);

    if (version >= 4) {
        const pending = await livePendingUpgrade(contract);
        if (pending !== null) {
            console.error(`ERROR: an upgrade to ${pending} is already pending. Execute or cancel it first.`);
            process.exit(1);
        }
    }

    const factory = await ethers.getContractFactory("vBTCb");
    console.log("\nChecking storage layout and deploying the new implementation...");
    const implementation = await upgrades.prepareUpgrade(proxy, factory, { kind: "uups" });
    console.log("New implementation:", implementation);

    const adminNonce = await contract.adminNonce();
    const request = { network: network.name, chainId: chainId.toString(), proxy, implementation, adminNonce: adminNonce.toString(), messageVersion: version };

    if (version === 3) {
        // V3: upgradeWithValidatorApproval, applied immediately, EIP-191 over keccak256(encodePacked(...)).
        request.hash = ethers.solidityPackedKeccak256(
            ["string", "address", "uint256", "uint256", "address"],
            ["UPGRADE", implementation, adminNonce, chainId, proxy]
        );
        request.signing = "EIP-191 personal_sign over the 32-byte hash";
    } else {
        // V4+: proposeUpgrade (EIP-712), then executeUpgrade after upgradeDelay().
        const domain = { name: "vBTCb", version: "4", chainId: chainId.toString(), verifyingContract: proxy };
        const value = { implementation, adminNonce: adminNonce.toString() };
        request.typedData = { domain, primaryType: "ProposeUpgrade", types: PROPOSE_UPGRADE_TYPES, value };
        request.hash = ethers.TypedDataEncoder.hash(domain, PROPOSE_UPGRADE_TYPES, value);
        request.signing = "EIP-712 typed data (ProposeUpgrade)";
        request.upgradeDelaySeconds = (await contract.upgradeDelay()).toString();
    }

    fs.writeFileSync(requestPath(), JSON.stringify(request, null, 2));

    const validators = await contract.getValidators();
    const required = await contract.requiredMintSignatures();
    console.log(`\nValidators (${required} of ${validators.length} must sign):`);
    validators.forEach((v) => console.log("  " + v));
    console.log("\nAdmin nonce:", adminNonce.toString());
    console.log("Signing:", request.signing);
    console.log("Hash to sign:", request.hash);
    console.log("\nWrote", requestPath());
    console.log("Next: collect the signatures, then run scripts/submit-upgrade.js with SIGNATURES=0x..,0x..");
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });
