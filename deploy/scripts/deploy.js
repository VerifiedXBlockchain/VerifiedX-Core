const { ethers, upgrades } = require("hardhat");

async function main() {
    console.log("=== VBTCbV2 Deployment to Base Sepolia ===\n");

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
    const VBTCbV2 = await ethers.getContractFactory("VBTCbV2");
    
    console.log("Deploying VBTCbV2 as UUPS proxy...");
    const proxy = await upgrades.deployProxy(
        VBTCbV2,
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
    
    // Read back state to verify
    const count = await proxy.validatorCount();
    const requiredMint = await proxy.requiredMintSignatures();
    const requiredRemove = await proxy.requiredRemoveSignatures();
    const decimals = await proxy.decimals();
    const validators = await proxy.getValidators();

    console.log("\nContract state:");
    console.log("  Validator count:", count.toString());
    console.log("  Required mint signatures:", requiredMint.toString());
    console.log("  Required remove signatures:", requiredRemove.toString());
    console.log("  Decimals:", decimals.toString());
    console.log("  Validators on contract:", validators);

    console.log("\n=== NEXT STEPS ===");
    console.log("1. Set this environment variable on ALL VFX nodes:");
    console.log(`   set BASE_BRIDGE_V2_CONTRACT=${proxyAddress}`);
    console.log("2. Restart all VFX nodes");
    console.log("3. Verify on BaseScan: https://sepolia.basescan.org/address/" + proxyAddress);
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });