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
    /// Collects validator ECDSA signatures for VBTCbV2 <c>mintWithProof</c> after a VFX bridge lock confirms.
    /// </summary>
    public static class BaseBridgeAttestationService
    {
        private static readonly ConcurrentDictionary<string, MintAttestationState> Pending = new();

        /// <summary>Keccak256(abi.encodePacked(to, amount, lockId, nonce, chainId, contract)) — matches VBTCbV2.sol.</summary>
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
            try
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Not a validator node"));

                var chainLock = VBTCBridgeLockState.GetByLockId(request.LockId);
                if (chainLock == null)
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Bridge lock not found on-chain"));

                if (!string.Equals(chainLock.EvmDestination?.Trim(), request.EvmDestination?.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<(bool, string?, string?)>((false, null, "EVM destination mismatch"));

                if (chainLock.AmountSats != request.AmountSats)
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Amount mismatch"));

                if (string.IsNullOrEmpty(request.ContractAddress) || request.ChainId != Globals.BaseEvmChainId)
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Invalid contract or chain id"));

                var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);
                if (account == null)
                    return Task.FromResult<(bool, string?, string?)>((false, null, "Validator account not loaded"));

                var privHex = account.GetKey;
                var privBytes = HexByteUtility.HexToByte(privHex);
                var hash = ConstructMintMessageHash(
                    request.EvmDestination,
                    request.AmountSats,
                    request.LockId,
                    request.Nonce,
                    request.ChainId,
                    request.ContractAddress);
                var sigHex = ValidatorEthKeyService.EthSignMessageHex(hash, privBytes);
                return Task.FromResult<(bool, string?, string?)>((true, sigHex, null));
            }
            catch (Exception ex)
            {
                return Task.FromResult<(bool, string?, string?)>((false, null, ex.Message));
            }
        }

        /// <summary>Caster: request signatures from active validators over HTTP.</summary>
        public static async Task CollectMintAttestationsForLock(BridgeLockRecord record, int requiredSignatures)
        {
            if (!BaseBridgeService.IsV2MintBridge || record == null) return;

            var contract = BaseBridgeService.VBTCbV2ContractAddress;
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
                var url = $"http://{v.IPAddress.Replace("::ffff:", "")}:{Globals.APIPort}/vbtcapi/VBTC/SignMintAttestation";
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
                    if (Globals.IsBlockCaster && BaseBridgeService.IsV2MintBridge)
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
            var raw = v.ToByteArray(); // little endian
            if (raw.Length > 32) throw new ArgumentOutOfRangeException(nameof(v));
            var be = new byte[32];
            Array.Copy(raw, 0, be, 32 - raw.Length, raw.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(be);
            ms.Write(be, 0, 32);
        }
    }
}
