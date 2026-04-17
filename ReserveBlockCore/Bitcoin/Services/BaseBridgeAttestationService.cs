using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using Newtonsoft.Json;
using Nethereum.Util;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;
using System.Net.Http;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Collects validator ECDSA signatures for VBTCb <c>mintWithProof</c> after a VFX bridge lock confirms.
    /// </summary>
    public static class BaseBridgeAttestationService
    {
        private static readonly ConcurrentDictionary<string, MintAttestationState> Pending = new();

        /// <summary>Keccak256(abi.encodePacked(to, amount, lockId, nonce, chainId, contract)) — matches VBTCb.sol.</summary>
        public static byte[] ConstructMintMessageHash(string to, long amountSats, string lockId, long nonce, long chainId, string contractAddress)
        {
            using var ms = new MemoryStream();
            WriteAddress20(ms, to);
            WriteUint256(ms, new BigInteger(amountSats));
            var lid = Encoding.UTF8.GetBytes(lockId ?? "");
            ms.Write(lid, 0, lid.Length);
            WriteUint256(ms, new BigInteger(nonce));
            WriteUint256(ms, new BigInteger(chainId));
            WriteAddress20(ms, contractAddress);
            return Sha3Keccack.Current.CalculateHash(ms.ToArray());
        }

        public static MintAttestationState? GetAttestationState(string lockId) =>
            string.IsNullOrEmpty(lockId) ? null : Pending.TryGetValue(lockId.Trim(), out var s) ? s : null;

        /// <summary>Validator node: verify lock and return hex signature (0x + 130 hex) for mint message.</summary>
        public static Task<(bool success, string? signatureHex, string? error)> HandleMintAttestationRequest(MintAttestationRequest request)
        {
            const string TAG = "BaseBridgeAttestationService.HandleMintAttestationRequest";
            try
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    LogUtility.Log($"[BridgeAttest] REJECT: Not a validator node. lockId={request.LockId}", TAG);
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Not a validator node"));
                }

                LogUtility.Log($"[BridgeAttest] Processing attestation request: lockId={request.LockId}, evmDest={request.EvmDestination}, amountSats={request.AmountSats}, nonce={request.Nonce}, chainId={request.ChainId}, contract={request.ContractAddress}", TAG);

                var chainLock = VBTCBridgeLockState.GetByLockId(request.LockId);
                if (chainLock == null)
                {
                    LogUtility.Log($"[BridgeAttest] REJECT: Bridge lock NOT FOUND in consensus state. lockId={request.LockId}. The lock TX may not be confirmed yet on this validator.", TAG);
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Bridge lock not found on-chain"));
                }

                LogUtility.Log($"[BridgeAttest] Found lock in consensus state: lockId={chainLock.LockId}, owner={chainLock.OwnerAddress}, evmDest={chainLock.EvmDestination}, amountSats={chainLock.AmountSats}, lockTxHash={chainLock.LockTxHash}", TAG);

                if (!string.Equals(chainLock.EvmDestination?.Trim(), request.EvmDestination?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    LogUtility.Log($"[BridgeAttest] REJECT: EVM destination mismatch. Chain={chainLock.EvmDestination}, Request={request.EvmDestination}", TAG);
                    return Task.FromResult<(bool, string?, string?)>((false, null, "EVM destination mismatch"));
                }

                if (chainLock.AmountSats != request.AmountSats)
                {
                    LogUtility.Log($"[BridgeAttest] REJECT: Amount mismatch. Chain={chainLock.AmountSats}, Request={request.AmountSats}", TAG);
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Amount mismatch"));
                }

                if (string.IsNullOrEmpty(request.ContractAddress) || request.ChainId != Globals.BaseEvmChainId)
                {
                    LogUtility.Log($"[BridgeAttest] REJECT: Invalid contract or chainId. RequestContract={request.ContractAddress}, RequestChainId={request.ChainId}, ExpectedChainId={Globals.BaseEvmChainId}", TAG);
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Invalid contract or chain id"));
                }

                var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);
                if (account == null)
                {
                    LogUtility.Log($"[BridgeAttest] REJECT: Validator account not loaded for {Globals.ValidatorAddress}", TAG);
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Validator account not loaded"));
                }

                var privHex = account.GetKey;
                var privBytes = HexByteUtility.HexToByte(privHex);
                var derivedBaseAddr = ValidatorEthKeyService.DeriveBaseAddressFromAccount(Globals.ValidatorAddress);
                
                LogUtility.Log($"[BridgeAttest] All checks passed. Signing message hash. ValidatorVFX={Globals.ValidatorAddress}, ValidatorBase={derivedBaseAddr}, lockId={request.LockId}", TAG);

                var hash = ConstructMintMessageHash(
                    request.EvmDestination,
                    request.AmountSats,
                    request.LockId,
                    request.Nonce,
                    request.ChainId,
                    request.ContractAddress);
                
                LogUtility.Log($"[BridgeAttest] Message hash constructed: 0x{Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 16)}... Params: to={request.EvmDestination}, amount={request.AmountSats}, lockId={request.LockId}, nonce={request.Nonce}, chainId={request.ChainId}, contract={request.ContractAddress}", TAG);

                var sigHex = ValidatorEthKeyService.EthSignMessageHex(hash, privBytes);
                
                LogUtility.Log($"[BridgeAttest] SIGNED successfully. lockId={request.LockId}, sig={sigHex?.Substring(0, Math.Min(20, sigHex?.Length ?? 0))}..., signerBase={derivedBaseAddr}", TAG);

                return Task.FromResult<(bool, string?, string?)>((true, sigHex, null));
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[BridgeAttest] EXCEPTION during attestation for lockId={request?.LockId}: {ex.Message}\n{ex.StackTrace}", TAG);
                return Task.FromResult<(bool, string?, string?)>((false, null, ex.Message));
            }
        }

        /// <summary>Caster: request signatures from active validators over HTTP.</summary>
        public static async Task CollectMintAttestationsForLock(BridgeLockRecord record, int requiredSignatures)
        {
            if (!BaseBridgeService.IsBridgeConfigured || record == null) return;

            // Pre-check: verify the owner actually has sufficient vBTC balance for this lock
            var balCheck = await VBTCService.TryGetAvailableTransparentVbtcBalance(record.SmartContractUID, record.OwnerAddress);
            if (balCheck.success && balCheck.availableBalance < record.Amount)
            {
                LogUtility.Log($"[BaseBridgeAttestation] Owner {record.OwnerAddress} has insufficient vBTC balance ({balCheck.availableBalance}) for lock amount ({record.Amount}). Skipping attestation.", "BaseBridgeAttestationService");
                return;
            }

            var contract = BaseBridgeService.ContractAddress;
            var nonce = record.VfxLockBlockHeight > 0 ? record.VfxLockBlockHeight : Globals.LastBlock.Height;
            var state = new MintAttestationState
            {
                LockId = record.LockId,
                SmartContractUID = record.SmartContractUID,
                EvmDestination = record.EvmDestination,
                AmountSats = record.AmountSats,
                Nonce = nonce,
                ChainId = Globals.BaseEvmChainId,
                ContractAddress = contract,
                RequiredSignatures = requiredSignatures,
                CreatedAt = TimeUtil.GetTime(),
                Status = "Pending"
            };
            Pending[record.LockId] = state;

            record.MintNonce = nonce;
            record.RequiredSignatures = requiredSignatures;
            record.Status = BridgeLockStatus.AttestationPending;
            BridgeLockRecord.Save(record);

            var validators = VBTCValidatorRegistry.GetActiveValidators();
            var client = Globals.HttpClientFactory?.CreateClient() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

            foreach (var v in validators)
            {
                if (string.IsNullOrEmpty(v.IPAddress)) continue;
                var url = $"http://{v.IPAddress.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/Validator/SignMintAttestation";
                var body = JsonConvert.SerializeObject(new MintAttestationRequest
                {
                    LockId = record.LockId,
                    EvmDestination = record.EvmDestination,
                    AmountSats = record.AmountSats,
                    Nonce = nonce,
                    ChainId = Globals.BaseEvmChainId,
                    ContractAddress = contract,
                    SmartContractUID = record.SmartContractUID
                });
                try
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync(url, content);
                    var txt = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) continue;
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(txt);
                    if (jo["success"]?.ToObject<bool>() != true) continue;
                    var sig = jo["signature"]?.ToString();
                    var derived = ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(v.FrostPublicKey);
                    if (!string.IsNullOrEmpty(sig) && !string.IsNullOrEmpty(derived))
                        state.ValidatorSignatures[derived] = sig;
                }
                catch
                {
                }
            }

            if (state.IsReady)
            {
                state.Status = "Ready";
                record.ValidatorSignatures = new Dictionary<string, string>(state.ValidatorSignatures);
                record.Status = BridgeLockStatus.AttestationReady;
                BridgeLockRecord.Save(record);
            }

            Pending[record.LockId] = state;
        }

        /// <summary>Background: casters collect attestations for V2 locks that just confirmed on VFX.</summary>
        public static async Task ProcessPendingAttestationsLoop()
        {
            await Task.Delay(20_000);
            while (!Globals.StopAllTimers)
            {
                try
                {
                    if (Globals.IsBlockCaster && BaseBridgeService.IsBridgeConfigured)
                    {
                        var required = await BaseBridgeService.GetRequiredMintSignaturesFromChainAsync();
                        foreach (var rec in BridgeLockRecord.GetPendingV2Attestations())
                        {
                            if (rec.Status == BridgeLockStatus.AttestationReady) continue;
                            await CollectMintAttestationsForLock(rec, required);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[BaseBridgeAttestation] {ex.Message}", "BaseBridgeAttestationService.ProcessPendingAttestationsLoop");
                }
                await Task.Delay(45_000);
            }
        }

        private static void WriteAddress20(MemoryStream ms, string? addr)
        {
            var hex = (addr ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            var b = HexByteUtility.HexToByte(hex);
            if (b.Length != 20)
                throw new ArgumentException("Invalid EVM address length");
            ms.Write(b, 0, 20);
        }

        private static void WriteUint256(MemoryStream ms, BigInteger v)
        {
            if (v.Sign < 0) throw new ArgumentOutOfRangeException(nameof(v));
            var raw = v.ToByteArray(); // little-endian, may have leading zero byte for unsigned
            // Strip trailing zero byte that BigInteger adds for positive numbers that have high bit set
            var len = raw.Length;
            if (len > 1 && raw[len - 1] == 0) len--;
            if (len > 32) throw new ArgumentOutOfRangeException(nameof(v));
            // Convert to big-endian, right-aligned in 32 bytes (matches Solidity abi.encodePacked uint256)
            var be = new byte[32];
            for (int i = 0; i < len; i++)
                be[31 - i] = raw[i];
            ms.Write(be, 0, 32);
        }
    }
}
