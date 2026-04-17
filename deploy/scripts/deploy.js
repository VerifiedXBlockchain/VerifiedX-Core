const { ethers, upgrades } = require("hardhat");

async function main() {
    console.log("=== VBTCbV3 Deployment to Base Sepolia ===\n");

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
        "0xb54c87485040f60c8935C4525CbC16ab9D8694d4",  // Validator 1 Base Address
        "0x417C81B2ff3695536f662805fA7dC472811DD471",  // Validator 2 Base Address
        "0x17Eb720C218d611c8EBe713E0908c214328ef886",  // Validator 3 Base Address
    ];

    console.log("Initial validators:");
    initialValidators.forEach((v, i) => console.log(`  ${i + 1}. ${v}`));
    console.log();

    // Deploy as UUPS proxy
    const VBTCbV3 = await ethers.getContractFactory("VBTCbV3");
    
    console.log("Deploying VBTCbV3 as UUPS proxy...");
    const proxy = await upgrades.deployProxy(
        VBTCbV3,
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
        console.warn("  https://sepolia.basescan.org/address/" + proxyAddress);
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
    console.log("3. Verify on BaseScan: https://sepolia.basescan.org/address/" + proxyAddress);
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });