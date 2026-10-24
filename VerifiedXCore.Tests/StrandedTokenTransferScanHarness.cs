using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LiteDB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Lists every old-style TokenTransfer() (transaction ToAddress = Token_Base, recipient only in the data) below
    /// Globals.PathAuditRulesHeight whose data recipient had no account record when the block applied it. StateData.TokenTransfer
    /// keys the record it creates by tx.ToAddress, so those tokens sit on a Token_Base record and not with the recipient.
    ///
    /// Two sources, compared with each other:
    /// <list type="bullet">
    /// <item>a replay of the blocks, in their order, through each path of block application (StateData.UpdateTreis and what it
    /// calls) that creates an account record, without writing anything;</item>
    /// <item>the Token_Base records in the account state, each matched to its block by the state root it was written with.</item>
    /// </list>
    /// The replay's set of addresses with a record is also compared with the account state's keys, which checks the replayed
    /// paths. Inert unless STRANDED_SCAN_DB names a mainnet database folder; every database is opened READ-ONLY and never
    /// disposed (disposing a read-only database with a write-ahead log tries to checkpoint it). STRANDED_SCAN_OUT names the
    /// report folder; STRANDED_SCAN_COMPARE, if set, names a CSV (height,tx_hash,token,sender,recipient,amount) to diff against.
    /// </summary>
    public class StrandedTokenTransferScanHarness
    {
        // Recipients StateData.UpdateTreis skips for a TX, and CreditVfxUnshieldAsync for an unshield (which adds TW_Base).
        private static readonly HashSet<string> BaseAddresses = new() { "Adnr_Base", "DecShop_Base", "Topic_Base", "Vote_Base", "Reserve_Base", "Token_Base" };

        private record Stranded(long Height, string Hash, string Token, string Sender, string Recipient, decimal Amount, string StateRoot);

        [Fact]
        public void Run()
        {
            var dbDir = Environment.GetEnvironmentVariable("STRANDED_SCAN_DB");
            if (string.IsNullOrWhiteSpace(dbDir)) return;
            var outDir = Environment.GetEnvironmentVariable("STRANDED_SCAN_OUT");
            if (string.IsNullOrWhiteSpace(outDir)) outDir = dbDir;
            Directory.CreateDirectory(outDir);
            var comparePath = Environment.GetEnvironmentVariable("STRANDED_SCAN_COMPARE");

            var mapper = new BsonMapper(null, LegacyTypeNameBinder.Instance);
            mapper.RegisterType<DateTime>(
                value => value.ToString("o", CultureInfo.InvariantCulture),
                bson => DateTime.ParseExact(bson, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            mapper.RegisterType<DateTimeOffset>(
                value => value.ToString("o", CultureInfo.InvariantCulture),
                bson => DateTimeOffset.ParseExact(bson, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            LiteDatabase Open(string name)
            {
                var path = Path.Combine(dbDir, name);
                Assert.True(File.Exists(path), $"Database not found: {path}");
                return new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            }

            // Contracts that carry TokenDetails: TokenTransfer applies only to those. TokenDetails is written when the contract
            // is deployed, so the final record says whether it had them; the replay also requires the deploy to come first.
            var tokenContracts = new HashSet<string>();
            foreach (var doc in Open(DbContext.RSRV_DB_SCSTATE_TREI).GetCollection(DbContext.RSRV_SCSTATE_TREI).FindAll())
                if (doc["TokenDetails"].IsDocument) tokenContracts.Add(doc["SmartContractUID"].AsString);

            var stateKeys = new HashSet<string>();
            var tokenBaseRecords = new List<(string StateRoot, string Token, decimal Amount)>();
            foreach (var doc in Open(DbContext.RSRV_DB_ASTATE_TREI).GetCollection(DbContext.RSRV_ASTATE_TREI).FindAll())
            {
                var key = doc["Key"].AsString;
                stateKeys.Add(key);
                if (key != "Token_Base") continue;
                foreach (var ta in doc["TokenAccounts"].IsArray ? doc["TokenAccounts"].AsArray : new BsonArray())
                    tokenBaseRecords.Add((doc["StateRoot"].AsString, ta["SmartContractUID"].AsString, ta["Balance"].AsDecimal));
            }

            var withRecord = new HashSet<string>();
            var deployed = new HashSet<string>();
            var stranded = new List<Stranded>();
            var notes = new List<string>();
            var stateRootHeights = new Dictionary<string, List<long>>();
            long blocks = 0, lastHeight = -1, oldStyle = 0, oldStyleWithRecord = 0;
            var blockCol = Open(DbContext.RSRV_DB_NAME).GetCollection<Block>(DbContext.RSRV_BLOCKS);
            foreach (var block in blockCol.Query().OrderBy(b => b.Height).ToEnumerable())
            {
                if (block.Height >= Globals.PathAuditRulesHeight) break;
                blocks++;
                lastHeight = block.Height;
                if (block.StateRoot != null)
                {
                    if (!stateRootHeights.TryGetValue(block.StateRoot, out var hs)) stateRootHeights[block.StateRoot] = hs = new List<long>();
                    hs.Add(block.Height);
                }
                foreach (var tx in block.Transactions ?? new List<Transaction>())
                {
                    // Sender (and, in the genesis block, recipient): UpdateTreis and CreateGenesisWorldTrei.
                    if (block.Height == 0)
                    {
                        withRecord.Add(tx.FromAddress);
                        withRecord.Add(tx.ToAddress);
                        continue;
                    }
                    if (tx.FromAddress != "Coinbase_TrxFees" && tx.FromAddress != "Coinbase_BlkRwd"
                        && !PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType)
                        && !VerifiedXCore.Bitcoin.Attestation.BtcAttestTx.IsAttestationType(tx.TransactionType))
                        withRecord.Add(tx.FromAddress);

                    // Recipient of a plain transfer.
                    if (tx.TransactionType == TransactionType.TX && !BaseAddresses.Contains(tx.ToAddress))
                        withRecord.Add(tx.ToAddress);

                    var function = DispatchedFunction(tx);
                    if (function == "TokenDeploy()")
                    {
                        var scUid = ContractUid(tx);
                        if (scUid != null) deployed.Add(scUid);
                    }
                    else if (function == "TokenTransfer()")
                        ReplayTokenTransfer(tx, block, tokenContracts, deployed, withRecord, stranded, notes, ref oldStyle, ref oldStyleWithRecord);

                    if (tx.TransactionType == TransactionType.NFT_SALE)
                        ReplaySaleComplete(tx, withRecord);

                    if (tx.TransactionType == TransactionType.RESERVE)
                        ReplayRecover(tx, stateKeys, withRecord, notes);

                    if (tx.TransactionType == TransactionType.VFX_UNSHIELD && !BaseAddresses.Contains(tx.ToAddress) && tx.ToAddress != "TW_Base"
                        && PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _) && payload != null && payload.TryValidateStructure(out _))
                        withRecord.Add(tx.ToAddress);
                }
            }

            // Token_Base records, matched to transfers by block (state root), token and amount.
            var unmatchedReplay = new List<Stranded>(stranded);
            var unmatchedState = new List<string>();
            foreach (var rec in tokenBaseRecords)
            {
                var match = unmatchedReplay.FirstOrDefault(s => s.StateRoot == rec.StateRoot && s.Token == rec.Token && s.Amount == rec.Amount);
                if (match != null) { unmatchedReplay.Remove(match); continue; }
                var heights = stateRootHeights.TryGetValue(rec.StateRoot, out var hs) ? string.Join("/", hs) : "no block";
                unmatchedState.Add($"{rec.StateRoot} (block {heights}) {rec.Token} {rec.Amount.ToString(CultureInfo.InvariantCulture)}");
            }

            var report = new List<string>
            {
                $"mainnet: {blocks} blocks to height {lastHeight} (below PathAuditRulesHeight {Globals.PathAuditRulesHeight})",
                $"old-style TokenTransfer() on a token contract: {oldStyle}; recipient had a record: {oldStyleWithRecord}; stranded: {stranded.Count} " +
                    $"({stranded.Select(s => s.Recipient).Distinct().Count()} recipients, {stranded.Select(s => (s.Token, s.Recipient)).Distinct().Count()} token and recipient pairs)",
                $"Token_Base records in the account state: {tokenBaseRecords.Count}; matched to a replayed stranded transfer: {tokenBaseRecords.Count - unmatchedState.Count}",
                $"replayed stranded transfers without a Token_Base record: {unmatchedReplay.Count}",
                $"Token_Base records without a replayed stranded transfer: {unmatchedState.Count}",
            };
            report.AddRange(unmatchedReplay.Select(s => $"  replay only: {s.Height} {s.Hash}"));
            report.AddRange(unmatchedState.Select(s => $"  state only: {s}"));

            // The replayed paths against the account state.
            var replayOnly = withRecord.Where(a => !stateKeys.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            var stateOnly = stateKeys.Where(a => !withRecord.Contains(a) && a != "Token_Base").OrderBy(a => a, StringComparer.Ordinal).ToList();
            report.Add($"addresses with a record: replay {withRecord.Count}, account state {stateKeys.Count} distinct keys; replay only {replayOnly.Count}, state only {stateOnly.Count} (Token_Base aside)");
            report.AddRange(replayOnly.Take(50).Select(a => $"  replay only: {a}"));
            report.AddRange(stateOnly.Take(50).Select(a => $"  state only: {a}"));
            report.AddRange(notes.Select(n => $"note: {n}"));

            if (!string.IsNullOrWhiteSpace(comparePath))
                report.AddRange(Compare(stranded, comparePath));

            File.WriteAllLines(Path.Combine(outDir, "stranded-token-transfers-report.txt"), report);
            File.WriteAllLines(Path.Combine(outDir, "stranded-token-transfers.csv"),
                new[] { "height,tx_hash,token,sender,recipient,amount" }.Concat(stranded.Select(s =>
                    $"{s.Height},{s.Hash},{s.Token},{s.Sender},{s.Recipient},{s.Amount.ToString(CultureInfo.InvariantCulture)}")));
        }

        /// <summary>The function UpdateTreis dispatches for a contract transaction type (array payload first, then object).</summary>
        private static string? DispatchedFunction(Transaction tx)
        {
            if (!(tx.TransactionType == TransactionType.NFT_TX || tx.TransactionType == TransactionType.NFT_MINT || tx.TransactionType == TransactionType.NFT_BURN
                || tx.TransactionType == TransactionType.FTKN_MINT || tx.TransactionType == TransactionType.FTKN_TX || tx.TransactionType == TransactionType.FTKN_BURN
                || tx.TransactionType == TransactionType.TKNZ_MINT || tx.TransactionType == TransactionType.TKNZ_TX || tx.TransactionType == TransactionType.TKNZ_BURN
                || tx.TransactionType == TransactionType.SC_MINT || tx.TransactionType == TransactionType.SC_TX || tx.TransactionType == TransactionType.SC_BURN
                || tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE || tx.TransactionType == TransactionType.TKNZ_WD_ARB
                || tx.TransactionType == TransactionType.TKNZ_WD_OWNER))
                return null;
            try { return (string?)JsonConvert.DeserializeObject<JArray>(tx.Data)![0]["Function"]; } catch { }
            try { return JObject.Parse(tx.Data)["Function"]?.ToObject<string?>(); } catch { }
            return null;
        }

        private static string? ContractUid(Transaction tx)
        {
            try { return (string?)JsonConvert.DeserializeObject<JArray>(tx.Data)![0]["ContractUID"]; } catch { }
            try { return JObject.Parse(tx.Data)["ContractUID"]?.ToObject<string?>(); } catch { }
            return null;
        }

        /// <summary>
        /// StateData.TokenTransfer below TokenTransferRecipientFirstHeight: on a contract with TokenDetails, a data recipient
        /// with no record gets none; a record keyed by tx.ToAddress is created instead.
        /// </summary>
        private static void ReplayTokenTransfer(Transaction tx, Block block, HashSet<string> tokenContracts, HashSet<string> deployed,
            HashSet<string> withRecord, List<Stranded> stranded, List<string> notes, ref long oldStyle, ref long oldStyleWithRecord)
        {
            if (block.Height >= Globals.TokenTransferRecipientFirstHeight)
                throw new InvalidOperationException("The recipient-first token transfer rule is active below the path-audit height.");
            JObject jobj;
            try { jobj = JObject.Parse(tx.Data); } catch { return; } // an array payload throws there: nothing applied
            string? scUid, toAddress; decimal? amount; string? fromAddress;
            try
            {
                scUid = jobj["ContractUID"]?.ToObject<string?>();
                toAddress = jobj["ToAddress"]?.ToObject<string?>();
                amount = jobj["Amount"]?.ToObject<decimal?>();
                fromAddress = jobj["FromAddress"]?.ToObject<string?>();
            }
            catch { return; }
            if (scUid == null || !tokenContracts.Contains(scUid)) return;
            if (!deployed.Contains(scUid))
            {
                notes.Add($"{block.Height} {tx.Hash}: TokenTransfer() on {scUid} before any TokenDeploy() of it in the blocks; replayed as a token contract");
            }
            var isOldStyle = tx.ToAddress != toAddress;
            if (isOldStyle) oldStyle++;
            if (toAddress != null && withRecord.Contains(toAddress))
            {
                if (isOldStyle) oldStyleWithRecord++;
                return;
            }
            withRecord.Add(tx.ToAddress);
            if (!isOldStyle) return;
            if (tx.ToAddress != "Token_Base")
                notes.Add($"{block.Height} {tx.Hash}: transfer to {toAddress} credited a record keyed {tx.ToAddress}");
            if (fromAddress != tx.FromAddress)
                notes.Add($"{block.Height} {tx.Hash}: data sender {fromAddress} differs from the transaction's {tx.FromAddress}");
            stranded.Add(new Stranded(block.Height, tx.Hash, scUid, tx.FromAddress, toAddress ?? "", amount ?? 0M, block.StateRoot));
        }

        /// <summary>StateData.CompleteSaleSmartContract: the seller's and the royalty payee's records.</summary>
        private static void ReplaySaleComplete(Transaction tx, HashSet<string> withRecord)
        {
            if (string.IsNullOrWhiteSpace(tx.Data)) return;
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var function = (string?)jobj["Function"];
                if (function != "Sale_Complete()" && function != "M_Sale_Complete()") return;
                var royalty = jobj["Royalty"]?.ToObject<bool?>();
                var transactions = jobj["Transactions"]?.ToObject<List<Transaction>?>();
                if (transactions == null) return;
                if (royalty == true)
                {
                    var toSeller = transactions.Where(x => x.Data.Contains("1/2")).FirstOrDefault();
                    var toRoyaltyPayee = transactions.Where(x => x.Data.Contains("2/2")).FirstOrDefault();
                    if (toSeller != null) withRecord.Add(toSeller.ToAddress);
                    if (toRoyaltyPayee != null) withRecord.Add(toRoyaltyPayee.ToAddress);
                }
                else
                {
                    var toSeller = transactions.FirstOrDefault();
                    if (toSeller != null) withRecord.Add(toSeller.ToAddress);
                }
            }
            catch { } // the apply throws there too, before any record
        }

        /// <summary>
        /// StateData.RecoverReserveAccountTx below ReserveRecoverRulesHeight: the recovery address carried in the transaction
        /// gets a record. A pending payment swept on the way credits the recovery address recorded in state, which this
        /// replay cannot see; it is reported when that address differs.
        /// </summary>
        private static void ReplayRecover(Transaction tx, HashSet<string> stateKeys, HashSet<string> withRecord, List<string> notes)
        {
            if (string.IsNullOrWhiteSpace(tx.Data)) return;
            try
            {
                var jobj = JObject.Parse(tx.Data);
                if ((string?)jobj["Function"] != "Recover()") return;
                var recoveryAddress = jobj["RecoveryAddress"]!.ToObject<string>()!;
                withRecord.Add(recoveryAddress);
                notes.Add($"{tx.Height} {tx.Hash}: Recover() from {tx.FromAddress} to {recoveryAddress}");
            }
            catch { }
        }

        private static IEnumerable<string> Compare(List<Stranded> stranded, string comparePath)
        {
            var theirs = File.ReadAllLines(comparePath).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Split(','))
                .Select(f => new Stranded(long.Parse(f[0], CultureInfo.InvariantCulture), f[1], f[2], f[3], f[4],
                    decimal.Parse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture), ""))
                .ToList();
            var ours = stranded.ToDictionary(s => s.Hash);
            var theirsByHash = theirs.ToDictionary(s => s.Hash);
            var lines = new List<string> { $"compared with {comparePath}: {theirs.Count} rows there, {stranded.Count} here" };
            foreach (var t in theirs.Where(t => !ours.ContainsKey(t.Hash)))
                lines.Add($"  only in the CSV: {t.Height},{t.Hash},{t.Token},{t.Sender},{t.Recipient},{t.Amount}");
            foreach (var o in stranded.Where(o => !theirsByHash.ContainsKey(o.Hash)))
                lines.Add($"  only in the replay: {o.Height},{o.Hash},{o.Token},{o.Sender},{o.Recipient},{o.Amount}");
            foreach (var o in stranded.Where(o => theirsByHash.ContainsKey(o.Hash)))
            {
                var t = theirsByHash[o.Hash];
                if (t.Height != o.Height || t.Token != o.Token || t.Sender != o.Sender || t.Recipient != o.Recipient || t.Amount != o.Amount)
                    lines.Add($"  differs: replay {o.Height},{o.Token},{o.Sender},{o.Recipient},{o.Amount} / CSV {t.Height},{t.Token},{t.Sender},{t.Recipient},{t.Amount}");
            }
            if (lines.Count == 1) lines.Add("  identical: same transactions, heights, tokens, senders, recipients and amounts");
            return lines;
        }
    }
}
