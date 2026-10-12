using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>Fixed numbers of the attestation layer (docs/PLAN-vBTC-Bitcoin-Attestation.md, sections 3 and 10).</summary>
    public static class BtcAttestRules
    {
        /// <summary>From and To of the sender-less attestation transactions.</summary>
        public const string SystemAddress = "BTC_Attestation";
        /// <summary>Their Signature field: the authority is the validator signatures inside the data.</summary>
        public const string SignatureSentinel = "BTC_ATTESTED";

        /// <summary>The most headers one report may carry.</summary>
        public const int MaxHeaders = 500;
        /// <summary>
        /// A quiet report covers exactly this many Bitcoin blocks (about a day): when the next this-many blocks above
        /// the events-through height hold no vault event, the signers say so once, and the events-through height moves
        /// on. Without it a long stretch with no vault activity would leave that height, and everything measured from
        /// it (which blocks a new vault is watched from, what a validator must keep and rescan), arbitrarily far
        /// behind Bitcoin. The range is fixed by state, so there is nothing to choose.
        /// </summary>
        public const int QuietBlocks = 144;
        public const int MaxEventsPerReport = 2000;
        /// <summary>
        /// The size, in characters of canonical text without signatures, that a builder keeps one part of a report
        /// under. The transaction cap is Globals.MaxVbtcVaultTxSizeBytes (256 KB); the rest is room for signatures.
        /// </summary>
        public const int PartTextBudget = 170_000;
        /// <summary>
        /// The largest coinbase transaction a report builder carries as the anchor of a block's proofs. Coinbase
        /// transactions are a few hundred bytes; above this the builder lists the block's events instead. With the
        /// header and transaction limits it keeps the largest possible part inside the transaction size cap.
        /// </summary>
        public const int MaxCoinbaseProofBytes = 10_000;
        /// <summary>A coinbase output cannot be spent on Bitcoin for this many blocks.</summary>
        public const int CoinbaseMaturity = 100;
        /// <summary>VFX blocks per signer-set epoch (about two hours).</summary>
        public const int EpochBlocks = 600;
        public const int MinSigners = 3;
        /// <summary>
        /// How many already-reported Bitcoin blocks before a vault's creation are still open to a backfill report for
        /// it (about a day): a deposit sent before the vault's creation transaction was mined.
        /// </summary>
        public const int WatchBackBlocks = 144;

        /// <summary>Tests only.</summary>
        internal static int? DepthOverride;

        /// <summary>
        /// Blocks of proof-of-work that must sit on top of a Bitcoin block before its events count: 3 on testnet, 6 on
        /// mainnet. Enforced by consensus: an event is accepted only when its block is this far below the attested tip.
        /// </summary>
        public static int Depth => DepthOverride ?? (Globals.IsTestNet ? 3 : 6);

        public static string NetworkTag => Globals.IsTestNet ? "testnet" : "mainnet";

        /// <summary>H1 reached: the attestation transactions are accepted and applied.</summary>
        public static bool Recording(long vfxHeight) => vfxHeight >= Globals.BitcoinAttestationRecordHeight;
        /// <summary>H2 reached: balances and withdrawals follow the attested state.</summary>
        public static bool Enforced(long vfxHeight) => vfxHeight >= Globals.BitcoinAttestationHeight;

        /// <summary>The signer-set epoch a VFX height is in; -1 before recording starts.</summary>
        public static long EpochOf(long vfxHeight) =>
            vfxHeight < Globals.BitcoinAttestationRecordHeight ? -1 : (vfxHeight - Globals.BitcoinAttestationRecordHeight) / EpochBlocks;

        /// <summary>Whether this VFX height is the first block of an epoch (where the signer set is stored).</summary>
        public static bool IsEpochStart(long vfxHeight) =>
            vfxHeight >= Globals.BitcoinAttestationRecordHeight && (vfxHeight - Globals.BitcoinAttestationRecordHeight) % EpochBlocks == 0;

        /// <summary>Signatures needed from a set of this size: two thirds, rounded up, and never fewer than <see cref="MinSigners"/>.</summary>
        public static int RequiredSignatures(int setSize) => Math.Max(MinSigners, (2 * setSize + 2) / 3);
    }

    public sealed class BtcReportSignature
    {
        [JsonProperty(Order = 1)] public string ValidatorAddress { get; set; } = "";
        [JsonProperty(Order = 2)] public string Signature { get; set; } = "";
    }

    public sealed class BtcReportOutput
    {
        [JsonProperty(Order = 1)] public long Vout { get; set; }
        [JsonProperty(Order = 2)] public string ScriptHex { get; set; } = "";
        [JsonProperty(Order = 3)] public long Sats { get; set; }
    }

    /// <summary>One Bitcoin transaction of the reported block, with proof that it is there.</summary>
    public sealed class BtcReportEvent
    {
        /// <summary>The transaction's position in the block.</summary>
        [JsonProperty(Order = 1)] public long Position { get; set; }
        /// <summary>The transaction without witness data, hex. Null only for an oversize event.</summary>
        [JsonProperty(Order = 2)] public string? TxHex { get; set; }
        [JsonProperty(Order = 3)] public List<string>? Branch { get; set; }

        // Oversize form (a transaction too large to carry): what it spends and creates, on the signatures alone.
        [JsonProperty(Order = 4)] public string? Txid { get; set; }
        [JsonProperty(Order = 5)] public List<string>? Spends { get; set; }
        [JsonProperty(Order = 6)] public List<BtcReportOutput>? Creates { get; set; }
    }

    /// <summary>
    /// The content of a Bitcoin report transaction (plan section 3.1). A report is about one Bitcoin block: the headers
    /// that bury it, and the transactions in it that touch a vault. What those transactions do is derived by the verifier
    /// from the transactions themselves.
    /// </summary>
    public sealed class BtcReport
    {
        public const string ScopeAll = "all";

        [JsonProperty(Order = 1)] public int V { get; set; } = 1;
        /// <summary>"all" for a forward report; a vault's contract UID for a backfill report.</summary>
        [JsonProperty(Order = 2)] public string Scope { get; set; } = ScopeAll;
        /// <summary>The Bitcoin block the events are in; 0 for a quiet report or a header replacement, which carry no events.</summary>
        [JsonProperty(Order = 3)] public long BlockHeight { get; set; }
        /// <summary>
        /// Usually 0. When set, the headers do not extend the attested tip: they replace every attested header above
        /// this height (a Bitcoin reorganisation of blocks whose events have not been reported yet).
        /// </summary>
        [JsonProperty(Order = 4)] public long ReplaceAbove { get; set; }
        /// <summary>Headers, 80 bytes each in hex, oldest first.</summary>
        [JsonProperty(Order = 5)] public List<string> Headers { get; set; } = new();
        [JsonProperty(Order = 6)] public string? CoinbaseTxHex { get; set; }
        [JsonProperty(Order = 7)] public List<string>? CoinbaseBranch { get; set; }
        [JsonProperty(Order = 8)] public List<BtcReportEvent> Events { get; set; } = new();
        /// <summary>A block with more events than one transaction holds is reported in parts, applied in order from 1.</summary>
        [JsonProperty(Order = 9)] public int Part { get; set; } = 1;
        /// <summary>
        /// True on the last part of a block's report (and on every report that has only one). The parts that follow a
        /// mined part are built from the state it left, so the number of parts is not fixed in advance.
        /// </summary>
        [JsonProperty(Order = 10)] public bool Final { get; set; } = true;
        /// <summary>The stored signer set the signatures are checked against.</summary>
        [JsonProperty(Order = 11)] public long SignerEpoch { get; set; }
        [JsonProperty(Order = 12)] public List<BtcReportSignature>? Signatures { get; set; }

        private static readonly JsonSerializerSettings Canonical = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            MissingMemberHandling = MissingMemberHandling.Error,
        };

        /// <summary>
        /// The one text form of this report: fields in a fixed order, absent fields omitted, signatures in address order.
        /// A report transaction's Data must be exactly this text, so one report has one transaction hash.
        /// </summary>
        public string ToCanonicalJson()
        {
            if (Signatures != null)
                Signatures = Signatures.OrderBy(s => s.ValidatorAddress, StringComparer.Ordinal).ToList();
            return JsonConvert.SerializeObject(this, Canonical);
        }

        /// <summary>What the validators sign: the SHA-256 of the canonical text without the signatures, lowercase hex.</summary>
        public string Hash()
        {
            var held = Signatures;
            Signatures = null;
            try { return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this, Canonical)))).ToLowerInvariant(); }
            finally { Signatures = held; }
        }

        /// <summary>The message a validator signs for a report hash. It names the network, so a signature cannot be replayed on the other one.</summary>
        public static string SigningMessage(string reportHash) => $"VFX-BTC-REPORT-1|{BtcAttestRules.NetworkTag}|{reportHash}";

        /// <summary>Parses a report transaction's Data. False unless the text is exactly the canonical form of what it parses to.</summary>
        public static bool TryParse(string? data, out BtcReport? report, out string? reason)
        {
            report = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(data)) { reason = "a Bitcoin report carries no data"; return false; }
            try { report = JsonConvert.DeserializeObject<BtcReport>(data, Canonical); }
            catch (Exception ex) { reason = $"the Bitcoin report does not parse: {ex.Message}"; return false; }
            if (report == null) { reason = "the Bitcoin report does not parse"; return false; }
            report.Headers ??= new List<string>();
            report.Events ??= new List<BtcReportEvent>();
            if (!string.Equals(report.ToCanonicalJson(), data, StringComparison.Ordinal))
            {
                report = null;
                reason = "the Bitcoin report is not in its canonical form";
                return false;
            }
            return true;
        }
    }
}
