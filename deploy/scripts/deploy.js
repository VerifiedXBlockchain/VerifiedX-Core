const { ethers, upgrades } = require("hardhat");

async function main() {
    console.log("=== vBTCb Deployment to Base Mainnet ===\n");

    const [deployer] = await ethers.getSigners();
    console.log("Deployer address:", deployer.address);
    
    const balance = await ethers.provider.getBalance(deployer.address);
    console.log("Deployer balance:", ethers.formatEther(balance), "ETH\n");

    if (balance === 0n) {
        console.error("ERROR: Deployer has no ETH. Get Base Sepolia ETH from a faucet.");
        process.exit(1);
    }

    // ============================================================
    // IMPORTANT: Replace these with your 3 validator Base addresses!
    // Get them from: GET http://<validator-ip>:<port>/wallet/api/base-address/<vfxAddress>
    // ============================================================
    const initialValidators = [
        "0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6",  // Validator 1 Base Address
        "0x95bDa4f2009A2998871c0BA3852Dc62617672Ed7",  // Validator 2 Base Address
        "0x5C44Cf6E3a022a49904e6139a49ae72866c02440",  // Validator 3 Base Address
    ];

    console.log("Initial validators:");
    initialValidators.forEach((v, i) => console.log(`  ${i + 1}. ${v}`));
    console.log();

    // Deploy as UUPS proxy
    const vBTCb = await ethers.getContractFactory("vBTCb");
    
    console.log("Deploying vBTCb as UUPS proxy...");
    const proxy = await upgrades.deployProxy(
        vBTCb,
        [
            "Verified Bitcoin on Base",  // name
            "vBTC.b",                    // symbol
            initialValidators            // initial validator set
        ],
        {
            initializer: "initialize",
            kind: "uups"
        }
    );

    await proxy.waitForDeployment();
    const proxyAddress = await proxy.getAddress();

    console.log("\n=== DEPLOYMENT SUCCESSFUL ===");
    console.log("Proxy address (USE THIS):", proxyAddress);

    // Public RPCs sometimes lag right after a tx; eth_call can briefly return empty (BAD_DATA).
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    let count;
    let requiredMint;
    let requiredRemove;
    let decimals;
    let validators;
    const delaysMs = [0, 2000, 4000, 8000];
    let lastErr;
    for (const ms of delaysMs) {
        if (ms) await sleep(ms);
        try {
            count = await proxy.validatorCount();
            requiredMint = await proxy.requiredMintSignatures();
            requiredRemove = await proxy.requiredRemoveSignatures();
            decimals = await proxy.decimals();
            validators = await proxy.getValidators();
            lastErr = undefined;
            break;
        } catch (e) {
            lastErr = e;
        }
    }
    if (lastErr) {
        console.warn(
            "\nCould not read contract state yet (often public RPC lag). Deployment tx already succeeded — confirm on BaseScan:"
        );
        console.warn("  https://basescan.org/address/" + proxyAddress);
        console.warn("Underlying error:", lastErr.message || lastErr);
    } else {
        console.log("\nContract state:");
        console.log("  Validator count:", count.toString());
        console.log("  Required mint signatures:", requiredMint.toString());
        console.log("  Required remove signatures:", requiredRemove.toString());
        console.log("  Decimals:", decimals.toString());
        console.log("  Validators on contract:", validators);
    }

    console.log("\n=== NEXT STEPS ===");
    console.log("1. Set this environment variable on ALL VFX nodes:");
    console.log(`   set BASE_BRIDGE_V3_CONTRACT=${proxyAddress}`);
    console.log("2. Restart all VFX nodes");
    console.log("3. Verify on BaseScan: https://basescan.org/address/" + proxyAddress);
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });