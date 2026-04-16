const { ethers } = require("hardhat");

/**
 * Helper script to call mintWithProof() on the VBTCbV2 contract.
 * 
 * Usage:
 *   set DEPLOYER_PRIVATE_KEY=0xYourBasePrivateKey
 *   set PROXY_ADDRESS=0xYourProxyAddress
 *   set MINT_TO=0xYourBaseAddress
 *   set MINT_AMOUNT=100000
 *   set MINT_LOCK_ID=yourLockId
 *   set MINT_NONCE=12345
 *   set MINT_SIGS=0xsig1,0xsig2
 *   npx hardhat run scripts/mint.js --network baseSepolia
 */
async function main() {
    const proxyAddress = process.env.PROXY_ADDRESS;
    const mintTo = process.env.MINT_TO;
    const mintAmount = process.env.MINT_AMOUNT;
    const mintLockId = process.env.MINT_LOCK_ID;
    const mintNonce = process.env.MINT_NONCE;
    const mintSigs = process.env.MINT_SIGS; // comma-separated hex signatures

    if (!proxyAddress || !mintTo || !mintAmount || !mintLockId || !mintNonce || !mintSigs) {
        console.log("Missing environment variables. Required:");
        console.log("  PROXY_ADDRESS  - VBTCbV2 proxy contract address");
        console.log("  MINT_TO        - Recipient Base address");
        console.log("  MINT_AMOUNT    - Amount in satoshis (e.g. 100000 = 0.001 BTC)");
        console.log("  MINT_LOCK_ID   - Lock ID from VFX bridge lock");
        console.log("  MINT_NONCE     - VFX block height where lock confirmed");
        console.log("  MINT_SIGS      - Comma-separated validator signatures");
        process.exit(1);
    }

    const signatures = mintSigs.split(",").map(s => s.trim());

    console.log("=== mintWithProof ===");
    console.log("Contract:", proxyAddress);
    console.log("To:", mintTo);
    console.log("Amount:", mintAmount, "satoshis");
    console.log("Lock ID:", mintLockId);
    console.log("Nonce:", mintNonce);
    console.log("Signatures:", signatures.length);

    const abi = [
        "function mintWithProof(address to, uint256 amount, string calldata lockId, uint256 nonce, bytes[] calldata signatures) external",
        "function balanceOf(address account) external view returns (uint256)",
        "function decimals() external view returns (uint8)"
    ];

    const [signer] = await ethers.getSigners();
    const contract = new ethers.Contract(proxyAddress, abi, signer);

    console.log("\nSending mint transaction...");
    const tx = await contract.mintWithProof(
        mintTo,
        BigInt(mintAmount),
        mintLockId,
        BigInt(mintNonce),
        signatures
    );

    console.log("TX hash:", tx.hash);
    console.log("Waiting for confirmation...");
    
    const receipt = await tx.wait();
    console.log("Confirmed in block:", receipt.blockNumber);

    // Check balance
    const balance = await contract.balanceOf(mintTo);
    console.log("\nvBTC.b balance:", balance.toString(), "satoshis");
    console.log("vBTC.b balance:", Number(balance) / 1e8, "BTC");
}

main()
    .then(() => process.exit(0))
    .catch((error) => {
        console.error(error);
        process.exit(1);
    });