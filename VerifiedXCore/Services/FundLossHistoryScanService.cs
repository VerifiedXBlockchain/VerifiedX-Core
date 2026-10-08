using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Privacy;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Fund-loss audit (Oct 2026) history scan. Walks mined blocks in height order and reports every transaction that
    /// looks like a use of one of the audited paths BEFORE their fixes activate, so the fixes can be gated knowing
    /// whether any apply-side change would meet an exploited transaction on replay. Read-only and self-contained:
    /// it keeps its own small trackers (created contracts, withdrawal keys, the VFX shielded supply) instead of
    /// touching chain state, so it runs against a block database opened read-only.
    ///
    /// Items (numbering from the Oct 8 2026 vetting note):
    ///   1  VFX unshield / private transfer beyond the pool's shielded supply, or with a missing nullifier,
    ///      Merkle root, or outer recipient/amount that differ from the payload's
    ///   2  Sale_Complete / M_Sale_Complete naming a contract never created, or an inner payment that is not positive
    ///   3  Reserve Recover() with an empty RecoverySigScript or no SignatureTime
    ///   4  Update/Transfer/Evolve/Devolve body that adds a TokenizationV2 feature to a contract that is not a vault
    ///   5  Sale_Start() (not M_Sale_Start) carrying the "manual" bid signature
    ///   6  Withdrawal REQUEST reusing a (requester, UniqueId, contract) key
    ///   9  A rewriting function whose body changes the contract's Royalty feature
    ///  10  FTKN_TX sent from a reserve (xRBX) address
    /// </summary>
    public static class FundLossHistoryScanService
    {
        public sealed record Hit(long Height, string TxHash, TransactionType Type, string Item, string Reason);

        public sealed class Result
        {
            public long Blocks;
            public long Transactions;
            public long TipHeight = -1;
            public readonly List<Hit> Hits = new();
            public readonly SortedDictionary<string, long> Counters = new(StringComparer.Ordinal);
            public decimal FinalVfxShieldedSupply;
        }

        private sealed class ContractInfo
        {
            public bool IsVault;
            public (decimal Amount, string PayTo)? Royalty;
            public string? Body;
        }

        private static readonly string[] RewritingFunctions = { "Update()", "Transfer()", "Evolve()", "Devolve()", "ChangeEvolveStateSpecific()" };

        public static Result Scan(IEnumerable<Block> blocksInHeightOrder, Action<string>? progress = null, long progressEvery = 100_000)
        {
            var r = new Result();
            var contracts = new Dictionary<string, ContractInfo>(StringComparer.Ordinal);
            var withdrawalKeys = new HashSet<string>(StringComparer.Ordinal);
            decimal vfxSupply = 0M;

            foreach (var block in blocksInHeightOrder)
            {
                r.Blocks++;
                r.TipHeight = block.Height;
                if (progressEvery > 0 && block.Height > 0 && block.Height % progressEvery == 0)
                    progress?.Invoke($"height {block.Height}: {r.Hits.Count} hit(s), VFX shielded supply {vfxSupply}");

                foreach (var tx in block.Transactions ?? new List<Transaction>())
                {
                    r.Transactions++;
                    if (tx.FromAddress == "Coinbase_TrxFees" || tx.FromAddress == "Coinbase_BlkRwd") continue;
                    try
                    {
                        CheckTransaction(tx, block.Height, r, contracts, withdrawalKeys, ref vfxSupply);
                    }
                    catch (Exception ex)
                    {
                        r.Hits.Add(new Hit(block.Height, tx.Hash ?? "", tx.TransactionType, "SCAN-ERROR", ex.Message));
                    }
                }
            }
            r.FinalVfxShieldedSupply = vfxSupply;
            progress?.Invoke($"done at height {r.TipHeight}: {r.Hits.Count} hit(s), VFX shielded supply {vfxSupply}");
            return r;
        }

        private static void Count(Result r, string key)
        {
            r.Counters.TryGetValue(key, out var n);
            r.Counters[key] = n + 1;
        }

        private static void CheckTransaction(Transaction tx, long height, Result r, Dictionary<string, ContractInfo> contracts, HashSet<string> withdrawalKeys, ref decimal vfxSupply)
        {
            void Add(string item, string reason) => r.Hits.Add(new Hit(height, tx.Hash ?? "", tx.TransactionType, item, reason));

            // ── Contract creations: remember vault-ness, royalty and body ─────────────────────────────────────
            var createdUid = LedgerIntegrityRules.CreatedContractUid(tx);
            if (createdUid != null)
            {
                Count(r, "creations");
                var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
                var info = Describe(payload.Data);
                contracts[createdUid] = info;
                if (info.IsVault) Count(r, "creations.vault");
                if (info.Royalty != null) Count(r, "creations.royalty");
            }

            // ── Item 10: fungible token transfer from a reserve ────────────────────────────────────────────────
            if (tx.TransactionType == TransactionType.FTKN_TX && (tx.FromAddress?.StartsWith("xRBX") ?? false))
            {
                Count(r, "item10.ftkn_from_reserve");
                Add("10 FTKN_TX from reserve", $"{tx.FromAddress} -> {tx.ToAddress}");
            }

            // ── Items 2 and 5: NFT sales ──────────────────────────────────────────────────────────────────────
            if (tx.TransactionType == TransactionType.NFT_SALE && !string.IsNullOrEmpty(tx.Data))
            {
                JObject? jobj = null;
                try { jobj = JObject.Parse(tx.Data); } catch { }
                var function = jobj?["Function"]?.ToObject<string?>();
                var scUID = jobj?["ContractUID"]?.ToObject<string?>();
                if (function == "Sale_Start()" || function == "M_Sale_Start()")
                {
                    Count(r, "sales.start");
                    var bidSig = jobj?["BidSignature"]?.ToObject<string?>();
                    if (function == "Sale_Start()" && bidSig == "manual")
                    {
                        Count(r, "item5.manual_on_sale_start");
                        Add("5 Sale_Start with manual bid signature", $"contract {scUID}, from {tx.FromAddress}, to {jobj?["NextOwner"]}");
                    }
                }
                else if (function == "Sale_Complete()" || function == "M_Sale_Complete()")
                {
                    Count(r, "sales.complete");
                    if (string.IsNullOrEmpty(scUID) || !contracts.ContainsKey(scUID))
                    {
                        Count(r, "item2.complete_unknown_contract");
                        Add("2 Sale_Complete on a contract never created", $"contract {scUID}, from {tx.FromAddress}, inner payments {InnerPayments(jobj)}");
                    }
                    var inner = jobj?["Transactions"] as JArray;
                    if (inner != null)
                        foreach (var p in inner)
                        {
                            var amt = p["Amount"]?.ToObject<decimal?>();
                            if (amt is null || amt <= 0M)
                            {
                                Count(r, "item2.inner_non_positive");
                                Add("2 Sale_Complete inner payment not positive", $"contract {scUID}, inner amount {amt}, to {p["ToAddress"]}");
                            }
                        }
                }
            }

            // ── Item 3: reserve recovery without a recovery signature ────────────────────────────────────────
            if (tx.TransactionType == TransactionType.RESERVE && !string.IsNullOrEmpty(tx.Data))
            {
                JObject? jobj = null;
                try { jobj = JObject.Parse(tx.Data); } catch { }
                var function = jobj?["Function"]?.ToObject<string?>();
                if (function == "Recover()")
                {
                    Count(r, "reserve.recover");
                    var sig = jobj?["RecoverySigScript"]?.ToObject<string?>();
                    var sigTime = jobj?["SignatureTime"];
                    var recoveryAddress = jobj?["RecoveryAddress"]?.ToObject<string?>();
                    if (string.IsNullOrEmpty(sig) || sigTime == null || sigTime.Type == JTokenType.Null || string.IsNullOrEmpty(recoveryAddress))
                    {
                        Count(r, "item3.recover_without_signature");
                        Add("3 Recover() without recovery signature", $"reserve {tx.FromAddress} -> {recoveryAddress}, sig empty: {string.IsNullOrEmpty(sig)}, SignatureTime missing: {sigTime == null || sigTime.Type == JTokenType.Null}");
                    }
                }
                else if (function == "CallBack()")
                    Count(r, "reserve.callback");
            }

            // ── Items 4 and 9: body rewrites ──────────────────────────────────────────────────────────────────
            if (createdUid == null && !string.IsNullOrEmpty(tx.Data))
            {
                var payload = SmartContractDeployBinding.ReadPayload(tx.Data);
                if (payload.Function != null && RewritingFunctions.Contains(payload.Function) && !string.IsNullOrEmpty(payload.ContractUID))
                {
                    Count(r, "rewrites." + payload.Function.TrimEnd('(', ')'));
                    if (!contracts.TryGetValue(payload.ContractUID, out var stored))
                    {
                        Count(r, "rewrites.unknown_contract");
                        if (payload.Function != "Transfer()") // transfers of unknown contracts are a separate, older story
                            Add("2/9 rewrite of a contract never created", $"{payload.Function} on {payload.ContractUID} from {tx.FromAddress}");
                    }
                    else if (!string.IsNullOrEmpty(payload.Data) && !string.Equals(payload.Data, stored.Body, StringComparison.Ordinal))
                    {
                        Count(r, "rewrites.body_changed");
                        var carried = Describe(payload.Data);
                        if (carried.IsVault && !stored.IsVault)
                        {
                            Count(r, "item4.vault_added_by_rewrite");
                            Add("4 rewrite adds TokenizationV2 to a non-vault", $"{payload.Function} on {payload.ContractUID} from {tx.FromAddress}");
                        }
                        if (!RoyaltyEquals(carried.Royalty, stored.Royalty))
                        {
                            Count(r, "item9.royalty_changed_by_rewrite");
                            Add("9 rewrite changes the Royalty feature", $"{payload.Function} on {payload.ContractUID} from {tx.FromAddress}: {Fmt(stored.Royalty)} -> {Fmt(carried.Royalty)}");
                        }
                        stored.Body = payload.Data;
                        stored.IsVault = carried.IsVault || stored.IsVault; // a vault stays a vault for later comparisons
                        stored.Royalty = carried.Royalty;
                    }
                }
            }

            // ── Item 6: withdrawal UniqueId reuse ─────────────────────────────────────────────────────────────
            if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_REQUEST && !string.IsNullOrEmpty(tx.Data))
            {
                Count(r, "withdrawals.request");
                JObject? jobj = null;
                try { jobj = JObject.Parse(tx.Data); } catch { }
                var uniqueId = jobj?["UniqueId"]?.ToObject<string?>() ?? tx.Hash ?? "";
                var function = jobj?["Function"]?.ToObject<string?>();
                var scUids = new List<string>();
                if (function == Bitcoin.Services.VBTCService.MultiWithdrawalFunction)
                {
                    foreach (var input in jobj?["Inputs"] as JArray ?? new JArray())
                    {
                        var u = input["SCUID"]?.ToObject<string?>() ?? input["ContractUID"]?.ToObject<string?>();
                        if (!string.IsNullOrEmpty(u)) scUids.Add(u);
                    }
                }
                else
                {
                    var u = jobj?["ContractUID"]?.ToObject<string?>();
                    if (!string.IsNullOrEmpty(u)) scUids.Add(u);
                }
                foreach (var u in scUids)
                {
                    var key = $"{tx.FromAddress}|{uniqueId}|{u}";
                    if (!withdrawalKeys.Add(key))
                    {
                        Count(r, "item6.uniqueid_reuse");
                        Add("6 withdrawal UniqueId reuse", $"requester {tx.FromAddress}, UniqueId {uniqueId}, contract {u}");
                    }
                }
            }

            // ── Item 1: VFX shielded pool ─────────────────────────────────────────────────────────────────────
            if (tx.TransactionType == TransactionType.VFX_SHIELD)
            {
                Count(r, "privacy.vfx_shield");
                vfxSupply += tx.Amount;
            }
            else if (tx.TransactionType == TransactionType.VFX_UNSHIELD || tx.TransactionType == TransactionType.VFX_PRIVATE_TRANSFER)
            {
                Count(r, tx.TransactionType == TransactionType.VFX_UNSHIELD ? "privacy.vfx_unshield" : "privacy.vfx_private_transfer");
                PrivateTxPayload? payload = null;
                try { PrivateTxPayloadCodec.TryDecode(tx.Data, out payload, out _); } catch { }
                var fee = payload?.Fee ?? Globals.PrivateTxFixedFee;
                var debit = tx.TransactionType == TransactionType.VFX_UNSHIELD ? tx.Amount + fee : fee;
                if (debit > vfxSupply)
                {
                    Count(r, "item1.beyond_supply");
                    Add("1 VFX unshield beyond shielded supply", $"to {tx.ToAddress}, amount {tx.Amount} + fee {fee}, pool supply before {vfxSupply}");
                }
                if (payload == null)
                    Add("1 private payload undecodable", "payload did not decode");
                else
                {
                    var problems = new List<string>();
                    if (payload.NullsB64 == null || payload.NullsB64.Count == 0) problems.Add("no nullifier");
                    if (string.IsNullOrWhiteSpace(payload.MerkleRootB64)) problems.Add("no merkle root");
                    if (string.IsNullOrWhiteSpace(payload.ProofB64)) problems.Add("no proof");
                    if ((payload.SpentCommitmentTreePositions?.Count ?? 0) != (payload.NullsB64?.Count ?? 0)) problems.Add("spent positions != nullifiers");
                    if (tx.TransactionType == TransactionType.VFX_UNSHIELD)
                    {
                        if (!string.Equals(payload.TransparentOutput, tx.ToAddress, StringComparison.Ordinal)) problems.Add($"outer To {tx.ToAddress} != payload {payload.TransparentOutput}");
                        if (payload.TransparentAmount != tx.Amount) problems.Add($"outer Amount {tx.Amount} != payload {payload.TransparentAmount}");
                    }
                    if (problems.Count > 0)
                    {
                        Count(r, "item1.structure");
                        Add("1 private tx structure", $"to {tx.ToAddress}, amount {tx.Amount}: {string.Join("; ", problems)}");
                    }
                }
                vfxSupply -= debit;
            }
        }

        private static string InnerPayments(JObject? jobj)
        {
            var inner = jobj?["Transactions"] as JArray;
            if (inner == null) return "none";
            return string.Join(", ", inner.Select(p => $"{p["Amount"]} -> {p["ToAddress"]}"));
        }

        private static bool RoyaltyEquals((decimal, string)? a, (decimal, string)? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Value.Item1 == b.Value.Item1 && string.Equals(a.Value.Item2, b.Value.Item2, StringComparison.Ordinal);
        }

        private static string Fmt((decimal Amount, string PayTo)? r) => r == null ? "none" : $"{r.Value.Amount} to {r.Value.PayTo}";

        /// <summary>Decompiles a carried body and reads the two features the scan cares about. Never throws.</summary>
        private static ContractInfo Describe(string? body)
        {
            var info = new ContractInfo { Body = body };
            if (string.IsNullOrEmpty(body)) return info;
            try
            {
                var scMain = SmartContractMain.GenerateSmartContractInMemory(body);
                var features = scMain?.Features;
                if (features == null) return info;
                info.IsVault = features.Any(f => f != null && f.FeatureName == FeatureName.TokenizationV2);
                var royaltyFeat = features.FirstOrDefault(f => f != null && f.FeatureName == FeatureName.Royalty);
                if (royaltyFeat != null)
                {
                    RoyaltyFeature? rf = royaltyFeat.FeatureFeatures as RoyaltyFeature;
                    if (rf == null)
                    {
                        try { rf = Newtonsoft.Json.JsonConvert.DeserializeObject<RoyaltyFeature>(royaltyFeat.FeatureFeatures?.ToString() ?? ""); } catch { }
                    }
                    if (rf != null) info.Royalty = (rf.RoyaltyAmount, rf.RoyaltyPayToAddress ?? "");
                }
            }
            catch { }
            return info;
        }

        public static string WriteReport(Result r, string path, string network, DateTime startedUtc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("VerifiedX fund-loss audit (Oct 2026) history scan");
            sb.AppendLine($"Network: {network}");
            sb.AppendLine($"Started (UTC): {startedUtc:O}");
            sb.AppendLine($"Finished (UTC): {DateTime.UtcNow:O}");
            sb.AppendLine($"Tip height scanned: {r.TipHeight}");
            sb.AppendLine($"Blocks scanned: {r.Blocks}");
            sb.AppendLine($"Transactions scanned: {r.Transactions}");
            sb.AppendLine($"VFX shielded supply at tip (reconstructed): {r.FinalVfxShieldedSupply}");
            sb.AppendLine($"Hits: {r.Hits.Count}");
            foreach (var g in r.Hits.GroupBy(h => h.Item).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.AppendLine($"  {g.Key}: {g.Count()}");
            sb.AppendLine("Counters:");
            foreach (var (k, v) in r.Counters)
                sb.AppendLine($"  {k}: {v}");
            sb.AppendLine();
            foreach (var h in r.Hits)
                sb.AppendLine($"height={h.Height} tx={h.TxHash} type={h.Type} item=\"{h.Item}\" reason=\"{h.Reason}\"");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
    }
}
