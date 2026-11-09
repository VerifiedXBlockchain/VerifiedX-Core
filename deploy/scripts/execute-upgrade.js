const { ethers, upgrades, network } = require("hardhat");
const { requireProxy, contractAt, messageVersion } = require("./upgrade-common");

/**
 * Step 3 of a V4 upgrade: executes the pending upgrade once upgradeDelay() has passed. Anyone may run it.
 *
 * Usage (Windows cmd):
 *   set DEPLOYER_PRIVATE_KEY=0x...
 *   set PROXY_ADDRESS=0x...
 *   npx hardhat run scripts/execute-upgrade.js --network baseMainnet
 */
async function main() {
    const proxy = requireProxy();
    const [sender] = await ethers.getSigners();
    const contract = await contractAt(proxy, sender);
    if ((await messageVersion(contract)) < 4) throw new Error("This proxy has no timelocked upgrade (V3); use submit-upgrade.js");

    const pending = await contract.pendingImplementation();
    if (pending === ethers.ZeroAddress) {
        console.log("No upgrade pending.");
        return;
    }
    const eta = await contract.pendingUpgradeEta();
    const now = BigInt((await ethers.provider.getBlock("latest")).timestamp);
    console.log("Pending implementation:", pending);
    console.log("Executable from:", new Date(Number(eta) * 1000).toISOString());
    if (now < eta) {
        console.log(`Not yet: ${eta - now} seconds left.`);
        return;
    }

    const tx = await contract.executeUpgrade();
    console.log("TX hash:", tx.hash);
    await tx.wait();
    console.log("Implementation now:", await upgrades.erc1967.getImplementationAddress(proxy));
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });
