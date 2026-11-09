# vBTCb (vBTC.b on Base) — contract, tests and deployment

## What is here

| Path | What it is |
|---|---|
| `contracts/vBTCb.sol` | The current contract (V4). Deployed behind a UUPS proxy. |
| `contracts/legacy/vBTCbV3.sol` | The V3 source that is live behind the Base mainnet proxy, kept unchanged (contract renamed) so the tests can upgrade a real V3 proxy. Never deploy it. |
| `test/vBTCb.test.js` | Hardhat tests: every signed message type, the timelock, the validator set limits, and the V3 → V4 upgrade. |
| `scripts/deploy.js` | Deploys a **new** proxy (fresh network, e.g. Base Sepolia). |
| `scripts/prepare-upgrade.js`, `submit-upgrade.js`, `execute-upgrade.js` | Upgrade an **existing** proxy (Base mainnet). |
| `scripts/mint.js` | Manual `mintWithProof` with signatures you already have. |
| `.openzeppelin/<network>.json` | Records each deployed implementation's storage layout. Upgrades are checked against it. Keep it committed. |

### Deployed addresses

| Network | Proxy (what nodes and users use) | Notes |
|---|---|---|
| Base mainnet (8453) | `0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D` | V3 implementation `0xd2bB4BdA89A23c63eaaD464F15658845a2114Ffa`. Upgrade in place; never redeploy. |
| Base Sepolia (84532) | `0x1154DDB476A38c01A389E1324121E15Df6703D19` | V3, carries supply from the pre-reset testnet chain. To be replaced by a fresh V4 proxy. |
| Base Sepolia (84532) | `0x969972475c375BE4c9Fe8fc47A9F7145ec63A97C` | Older, unused. |

## How V4 is governed

There is no owner. The deployer account gets no rights. Everything privileged needs signatures from the validators
on the contract, and every message is EIP-712 typed data under the domain `("vBTCb", "4", chainId, proxy)`:

| Action | Message type | Signatures needed |
|---|---|---|
| Mint against a VFX lock | `Mint(to, amount, lockId, nonce)` | 2/3 of validators (at least 2) |
| Add validators | `AddValidators(validators[], vfxBlockHeight, adminNonce)` | 2/3, and each new validator signs the same message itself |
| Remove validators | `RemoveValidators(validators[], vfxBlockHeight, adminNonce)` | 2/3 |
| Propose removing validators that went dark | `ProposeRemoval(validators[], vfxBlockHeight, adminNonce)` | more than half |
| Propose a code upgrade | `ProposeUpgrade(implementation, adminNonce)` | 2/3 |
| Propose a new upgrade delay | `ProposeUpgradeDelay(delay, adminNonce)` | 2/3 |
| Cancel what is pending | `CancelPending(adminNonce)` | blocking minority: `validatorCount - requiredMintSignatures + 1` |

- No group short of 2/3 can change the set at once, so none can remove the others and become 2/3 of what is left.
- If so many validators go dark that 2/3 can no longer sign, a live majority proposes removing them. The removal waits
  `upgradeDelay()`, then anyone runs `executeRemoval()`. Meanwhile any blocking minority (the validators who could
  already refuse a mint) can cancel it, so a majority cannot use it against validators who are still running.
- A proposed upgrade waits `upgradeDelay()` (48 hours until changed) and is then executed by anyone with
  `executeUpgrade()`. Holders who disagree have that long to burn and exit.
- The delay can be set between 48 hours and 30 days. The change itself waits the current delay.
- A pending upgrade, delay change or removal lapses 14 days after it became executable; a lapsed one can be
  replaced by a new proposal.
- The set stays between 3 and 50 validators. Each admin message carries `adminNonce`, so a signature set is used once.

## Setup and tests

```cmd
cd C:\Users\Aaron\Documents\GitHub\VerifiedX-Core\deploy
npm install
npx hardhat compile
npx hardhat test
```

## Deploy a new proxy (Base Sepolia)

1. Get each validator's Base address:
   `GET http://<validator-ip>:<port>/wallet/api/base-address/<validatorVfxAddress>`
2. Deploy (any funded account pays; get Sepolia ETH from https://www.alchemy.com/faucets/base-sepolia):
   ```cmd
   set DEPLOYER_PRIVATE_KEY=0x...
   set VALIDATORS=0xA...,0xB...,0xC...
   npx hardhat run scripts/deploy.js --network baseSepolia
   ```
3. On every VFX node of that network set `BaseBridgeContract=<proxy>` in `config.txt` and restart.
   Note: Core writes the default address into `config.txt` only when the file is first created, so existing
   nodes keep the old address until edited.
4. Commit the updated `.openzeppelin/base-sepolia.json`.

## Upgrade an existing proxy (Base mainnet)

The proxy address, balances, used lock ids and validators all stay. Only the implementation behind the proxy changes.

1. **Prepare**: checks the storage layout against `.openzeppelin/base.json`, deploys the new implementation and
   writes `upgrade-request.baseMainnet.json` with the exact hash to sign.
   ```cmd
   set DEPLOYER_PRIVATE_KEY=0x...
   set PROXY_ADDRESS=0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D
   npx hardhat run scripts/prepare-upgrade.js --network baseMainnet
   ```
2. **Sign**: each validator on the contract signs on its own node, which holds the key. The node reads the
   contract's version and admin nonce itself, and returns the implementation's code hash to check against the
   build reviewed. The route answers localhost only (port 7294 on mainnet, 17294 on testnet):
   ```cmd
   curl -X POST http://127.0.0.1:7294/valapi/Validator/SignBaseAdminAction -H "Content-Type: application/json" -d "{\"Action\":\"UPGRADE\",\"Implementation\":\"0x...\"}"
   ```
   `UPGRADE` is the V3 message (EIP-191). On V4 the actions are `PROPOSE_UPGRADE` (with `Implementation`),
   `PROPOSE_UPGRADE_DELAY` (`DelaySeconds`), `PROPOSE_REMOVAL` (`Validators`, `VfxBlockHeight`) and `CANCEL_PENDING`.
   The `Digest` it returns must equal the hash in `upgrade-request.<network>.json`.
3. **Submit**: verifies the signatures locally first and sends nothing if there are too few.
   ```cmd
   set SIGNATURES=0xsig1,0xsig2
   npx hardhat run scripts/submit-upgrade.js --network baseMainnet
   ```
   - On the V3 proxy this is `upgradeWithValidatorApproval` and the upgrade happens immediately. This is the
     one and only upgrade without a delay: V4 removes that function.
   - On a V4 proxy this is `proposeUpgrade`; the script prints when it becomes executable.
4. **Execute** (V4 only, after the delay, anyone):
   ```cmd
   npx hardhat run scripts/execute-upgrade.js --network baseMainnet
   ```

The V3 → V4 upgrade changes the mint message from V3's packed hash to EIP-712. VFX nodes read `MESSAGE_VERSION()`
from the contract and sign the form it reads, so mints carry on across the upgrade without a gap.

## Testing the bridge (from a regular node)

### A. Get your Base address
```
GET http://localhost:<port>/wallet/api/base-address/<yourVfxAddress>
```

### B. Fund your Base address
Send ETH on the matching Base network to the `0x...` address from step A (you pay gas for minting on Base).

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
```
