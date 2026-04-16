# VBTCbV2 Deployment Guide (Windows)

## Prerequisites
- Node.js 18+ installed (verify: `node --version`)
- npm installed (verify: `npm --version`)
- A wallet with Base Sepolia ETH (get from faucet below)
- Your 3 validator Base addresses

## Get Base Sepolia ETH
- https://www.alchemy.com/faucets/base-sepolia
- https://faucet.quicknode.com/base/sepolia

## Step-by-Step (Windows Command Prompt)

### 1. Install dependencies
```cmd
cd C:\Users\Aaron\Documents\GitHub\VerifiedX-Core\deploy
npm install
```

### 2. Get your 3 validator Base addresses
On each validator node, call:
```
GET http://<validator-ip>:<port>/wallet/api/base-address/<validatorVfxAddress>
```
Record all three `0x...` addresses.

### 3. Edit the deploy script
Open `deploy\scripts\deploy.js` and replace the 3 placeholder addresses with your real validator Base addresses.

### 4. Set your deployer private key
```cmd
set DEPLOYER_PRIVATE_KEY=0xYourPrivateKeyHere
```
This is the private key of ANY wallet that has Base Sepolia ETH. It does NOT need to be a validator key.

### 5. Compile
```cmd
npx hardhat compile
```

### 6. Deploy
```cmd
npx hardhat run scripts/deploy.js --network baseSepolia
```

The script will output the **proxy address** — this is your `VBTCbV2ContractAddress`.

### 7. Configure ALL VFX nodes
On each VFX node (validators + regular nodes), set the environment variable before starting:
```cmd
set BASE_BRIDGE_V2_CONTRACT=0xYourProxyAddress
```
Then restart the node.

### 8. Verify deployment
Check on BaseScan: `https://sepolia.basescan.org/address/<proxyAddress>`

## Testing the Bridge (from a regular node)

### A. Get your Base address
```
GET http://localhost:<port>/wallet/api/base-address/<yourVfxAddress>
```

### B. Fund your Base address
Send Base Sepolia ETH to the `0x...` address from step A (you need ETH to pay gas for minting on Base).

### C. Lock vBTC on VFX
```
POST http://localhost:<port>/vbtcapi/VBTC/BridgeToBase
{
    "scUID": "<your-vbtc-contract-uid>",
    "ownerAddress": "<yourVfxAddress>",
    "amount": "0.001",
    "evmDestination": ""
}
```
Leave `evmDestination` empty — it auto-derives from your VFX key.

### D. Wait for attestation
```
GET http://localhost:<port>/vbtcapi/VBTC/GetMintAttestation/<lockId>
```
Poll until status is "Ready".

### E. Mint on Base
Use the mint helper script:
```cmd
set DEPLOYER_PRIVATE_KEY=0xYourBasePrivateKey
set PROXY_ADDRESS=0xYourProxyAddress
set MINT_TO=0xYourBaseAddress
set MINT_AMOUNT=100000
set MINT_LOCK_ID=lockIdFromAttestation
set MINT_NONCE=12345
set MINT_SIGS=0xsig1,0xsig2
npx hardhat run scripts/mint.js --network baseSepolia
```

### F. Verify balance
```
GET http://localhost:<port>/wallet/api/btc/base-balances