const { ethers, upgrades, network } = require("hardhat");

/**
 * Deploys a NEW vBTCb proxy (current contracts/vBTCb.sol) with the given validator set.
 * Use this for a fresh network (Base Sepolia). Base mainnet already has a proxy: upgrade it with
 * prepare-upgrade.js / submit-upgrade.js instead, or every holder's balance is left on the old one.
 *
 * Usage (Windows cmd):
 *   set DEPLOYER_PRIVATE_KEY=0x...      any funded account; it gets no rights on the contract
 *   set VALIDATORS=0xA...,0xB...,0xC... the validators' Base addresses (at least 3)
 *   set CASTERS=0xD...,0xE...,0xF...    the VFX casters' Base addresses (3 to 10); a validator may be on both
 *   npx hardhat run scripts/deploy.js --network baseSepolia
 *
 * Each validator's Base address: GET http://<validator>:<port>/wallet/api/base-address/<validatorVfxAddress>
 */
async function main() {
    const { chainId } = await ethers.provider.getNetwork();
    console.log(`=== vBTCb deployment to ${network.name} (chain ${chainId}) ===\n`);

    // Base mainnet already has the vBTC.b proxy that holders hold. A second one would be a different token, and the
    // next steps below would point nodes at it.
    if (chainId === 8453n && process.env.ALLOW_SECOND_MAINNET_PROXY !== "yes") {
        console.error("ERROR: Base mainnet already has the vBTC.b proxy 0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D.");
        console.error("Upgrade it with prepare-upgrade.js / submit-upgrade.js. Nothing was deployed.");
        process.exit(1);
    }

    const initialValidators = (process.env.VALIDATORS || "")
        .split(",")
        .map((v) => v.trim())
        .filter((v) => v.length > 0)
        .map((v) => ethers.getAddress(v));
    if (initialValidators.length < 3) {
        console.error("ERROR: set VALIDATORS to at least 3 comma-separated validator Base addresses.");
        process.exit(1);
    }
    const initialCasters = (process.env.CASTERS || "")
        .split(",")
        .map((v) => v.trim())
        .filter((v) => v.length > 0)
        .map((v) => ethers.getAddress(v));
    if (initialCasters.length < 3 || initialCasters.length > 10) {
        console.error("ERROR: set CASTERS to 3 to 10 comma-separated caster Base addresses.");
        process.exit(1);
    }

    const [deployer] = await ethers.getSigners();
    const balance = await ethers.provider.getBalance(deployer.address);
    console.log("Deployer address:", deployer.address);
    console.log("Deployer balance:", ethers.formatEther(balance), "ETH\n");
    if (balance === 0n) {
        console.error("ERROR: Deployer has no ETH on this network.");
        process.exit(1);
    }

    console.log("Initial validators:");
    initialValidators.forEach((v, i) => console.log(`  ${i + 1}. ${v}`));
    console.log("Initial casters:");
    initialCasters.forEach((v, i) => console.log(`  ${i + 1}. ${v}`));
    console.log();

    const vBTCb = await ethers.getContractFactory("vBTCb");
    console.log("Deploying vBTCb as UUPS proxy...");
    const proxy = await upgrades.deployProxy(vBTCb, ["Verified Bitcoin on Base", "vBTC.b", initialValidators, initialCasters], {
        initializer: "initialize",
        kind: "uups",
    });
    await proxy.waitForDeployment();
    const proxyAddress = await proxy.getAddress();

    console.log("\n=== DEPLOYMENT SUCCESSFUL ===");
    console.log("Proxy address (USE THIS):", proxyAddress);
    console.log("Implementation:", await upgrades.erc1967.getImplementationAddress(proxyAddress));

    // Public RPCs sometimes lag right after a tx; eth_call can briefly return empty (BAD_DATA).
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    let lastErr;
    for (const ms of [0, 2000, 4000, 8000]) {
        if (ms) await sleep(ms);
        try {
            console.log("\nContract state:");
            console.log("  Message version:", (await proxy.MESSAGE_VERSION()).toString());
            console.log("  Validators on contract:", await proxy.getValidators());
            console.log("  Required mint signatures:", (await proxy.requiredMintSignatures()).toString());
            console.log("  Required remove signatures:", (await proxy.requiredRemoveSignatures()).toString());
            console.log("  Casters on contract:", await proxy.getCasters());
            console.log("  Required caster signatures:", (await proxy.requiredCasterSignatures()).toString());
            console.log("  Upgrade delay (s):", (await proxy.upgradeDelay()).toString());
            lastErr = undefined;
            break;
        } catch (e) {
            lastErr = e;
        }
    }
    if (lastErr) {
        console.warn("\nCould not read contract state yet (often public RPC lag). The deployment tx succeeded.");
        console.warn("Underlying error:", lastErr.message || lastErr);
    }

    const explorer = chainId === 8453n ? "https://basescan.org" : "https://sepolia.basescan.org";
    console.log("\n=== NEXT STEPS ===");
    console.log(`1. On every VFX node of this network set in config.txt:  BaseBridgeContract=${proxyAddress}`);
    console.log("2. Restart the nodes");
    console.log(`3. Verify on ${explorer}/address/${proxyAddress}`);
    console.log("4. Commit deploy/.openzeppelin/*.json: it records the storage layout later upgrades are checked against");
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });
