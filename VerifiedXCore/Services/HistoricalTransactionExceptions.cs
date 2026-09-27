using VerifiedXCore.Models;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// VX-02 / NEW-04 / NEW-05 / NEW-09 (follow-up, owner decision 25 Sep 2026): mainnet transactions that were accepted
    /// when mined but that rules added by the security remediation now refuse. They were early testing and carry no value
    /// the owner cares about; the rules have no activation height, so without this list a node syncing from genesis stops
    /// at the first one (block 3,202,099).
    ///
    /// Each entry is honoured only in block validation, only in the block at its own height, and only for the content its
    /// hash commits to - so it cannot be replayed, re-proposed or re-shaped, and every rule stays in force for everything
    /// else. Honoured entries skip VerifyTX and the per-block debit guard, exactly as they did when they were mined; their
    /// state apply is unchanged. Source: the read-only rule scan of the mainnet chain to block 6,891,066 (35 rule hits on
    /// 32 transactions) plus what the full replay through block validation finds: a NEW-07 same-block overspend (5,655,096)
    /// and the last V1 withdrawal request (5,662,203), whose lead-arbiter rule depends on arbiter signing addresses each
    /// node fetches over HTTP at startup - with arbiters retired (NEW-28) no syncing node could ever accept it.
    ///
    /// Testnet (owner decision, 25 Sep 2026): the bridge exit and its completion of 12 May 2026. The pre-audit bridge rule
    /// b61d49f5 (13 Sep) requires a committee-caster sender from testnet height 1; for these old heights the committee falls
    /// back to today's seed casters, and the sender (a caster that voted on the exit at the time) is not one, so a fresh
    /// testnet sync stopped at 64,607. They are the only bridge exits on testnet.
    /// </summary>
    public static class HistoricalTransactionExceptions
    {
        private static readonly IReadOnlyDictionary<string, long> Mainnet = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["d4e01cc7754622be5018315ef34976cf9d6fba4245125995dc34127392efba7e"] = 3_202_099, // FTKN_TX: NEW-04 token transfer binding
            ["f9aac48bba27940715c912c99e8f750093064de93c61da051251ff02a86d75ab"] = 3_205_974, // FTKN_BURN: NEW-04 token burn binding
            ["923ca0f5939e1a5c12879acf1afc5ad56a14123d06f98f858150c540b73697d6"] = 3_205_975, // FTKN_BURN: NEW-04 token burn binding
            ["e07f9f8a06fd200b5082d6397b05f9898d5b6a52200257bea3069e6185ec2f65"] = 3_205_976, // FTKN_BURN: NEW-04 token burn binding
            ["ea54b664bd7964526ce65f70abb7806252f88cbaf4f327185f873c75e994c2c0"] = 3_711_214, // FTKN_BURN: NEW-04 token burn binding
            ["b3085e18f9c0a893efbce0f495414e48f52822a3256910043d96793cce85955e"] = 3_711_215, // FTKN_BURN: NEW-04 token burn binding
            ["125b4dec9697af02b23722a59c70ca45a0dceaedfd087f3076f2e7637c9b6fc5"] = 3_711_218, // FTKN_BURN: NEW-04 token burn binding
            ["46af91804504e03e674bc615903c71eb0cd9e87722413905dacf5c1a3f1d58a3"] = 3_711_218, // FTKN_BURN: NEW-04 token burn binding
            ["b30ad8d63651edd823cd1d21e7ee0a202cc6a4bc017f125ba69d05e77ae42378"] = 3_711_220, // FTKN_BURN: NEW-04 token burn binding
            ["10d1e5ad0a5dd7eb0c7fed9ab6c4924e559462868c1796c7a39db584a9058d0d"] = 4_108_774, // TKNZ_TX: NEW-05 V1 multi input
            ["c3ef49c822be8d1b23e7f9a51224d800c74f789ca460f408110a197deff5f8dc"] = 4_108_774, // TKNZ_TX: NEW-05 V1 multi input
            ["39fd75356ecd93a859f48407ac44465651d4e3585d2801f6b9f998677d7731c4"] = 4_108_776, // TKNZ_TX: NEW-05 V1 multi input
            ["2e635d5430d610b128b8ca99c81f032c3aa88f2915261827a0c41d5a6543814c"] = 4_108_778, // TKNZ_TX: NEW-05 V1 multi input
            ["5ecf8a0a566c71374b613ea19aff03afe017982989f8928bcbff19c6357d8b1c"] = 4_108_784, // TKNZ_TX: NEW-05 V1 multi input
            ["a8fd378c8607384c437fc1121dbc74ef81db44fd41b61e161627c7b7c6e3f3a8"] = 4_108_784, // TKNZ_TX: NEW-05 V1 multi input
            ["a39d2720784b8af2d61bd26b5208ca54192f16fcfafb9ebba4f3f516635f37b3"] = 4_116_860, // NFT_MINT: VX-02 binding (Mint)
            ["49d23bf3cd8aa7999a2011dc4168db7b9a853f3c57ee0502fa2d5a85092863c6"] = 4_135_970, // TKNZ_TX: NEW-05 V1 multi input
            ["6490d1eca86508cb499d39a6e53322dc5d5be2c32bd13b4b19c2c12740b5547e"] = 4_136_029, // TKNZ_TX: NEW-05 V1 multi input
            ["27f385685dce06b2b35b5beb7fdd1202da52b22644db9fa47d3ddf9666774b5f"] = 4_136_412, // TKNZ_TX: NEW-05 V1 multi input
            ["b5d2cbe265891d3eb337fbc1a3d6c98f0027898409de0f53053ecb8718e82183"] = 4_136_480, // TKNZ_TX: NEW-05 V1 multi input
            ["a59e788fa65a0ac902c12d14271a9e201ec1315f738ab3edfe763d1567b61129"] = 4_782_632, // TKNZ_MINT: VX-02 binding (TokenDeploy)
            ["6e4765504b8b55685c4a8a804d464dd515c66d62c5854a335654343fd4805333"] = 4_874_201, // NFT_MINT: VX-02 binding (Mint)
            ["4659abcc0ab1d11e1478eb7fced2b74adc7ded03b750f99b5e9271cc2ccc5f20"] = 5_341_154, // TKNZ_TX: NEW-05 V1 multi input
            ["fab15ebf1ef2561a10c470d48108960c600a69a5a62cf70fcec1ac5f28ee3b70"] = 5_639_293, // TKNZ_TX: NEW-05 V1 amount; NEW-05 V1 missing contract
            ["64564285c45bd532ce261a058301f69832a9dbd2bcf35effc9ce645794bf1ad5"] = 5_639_311, // TKNZ_TX: NEW-05 V1 missing contract
            ["e61607aeb3023e855d1b5da1e902f7ed9651caf9f2cd7ef73c74333f06e8a506"] = 5_639_321, // TKNZ_TX: NEW-05 V1 amount; NEW-05 V1 missing contract
            ["42bdf36337db3fae46db54798a2c44aaacbff193733b599170f4c182dad95287"] = 5_639_431, // TKNZ_TX: NEW-05 V1 amount; NEW-05 V1 missing contract
            ["c0f7725248ef8d6cc1aad8eeaa6a19906a41459aa2917a36587571db68367f05"] = 5_639_485, // TKNZ_TX: NEW-05 V1 amount
            ["e1bccde2130990680f5d3fb630862ca4d5df517ccc3806dae7cf556542683ccd"] = 5_639_485, // TKNZ_TX: NEW-05 V1 amount
            ["ab7e9d2780e41f4e8f0d56ff6a135da228f245a3941f0cba0e1d798b6002b6f9"] = 5_639_511, // TKNZ_TX: NEW-05 V1 amount
            ["3a1e91e679e42b341a871282f97124607356674968b71c7a7f68f011e7355240"] = 5_639_751, // TKNZ_TX: NEW-05 V1 amount
            ["506179cce4e79cce6082b93928f689087da5226dc41253ece18b1523ff2e292a"] = 5_646_388, // TKNZ_TX: NEW-05 V1 amount
            // Found by the full replay through block validation:
            ["460f23b0fb8afe0ee761b686a2e047adb64796cc140fbf0fc23a530d2765092e"] = 5_655_096, // FTKN_TX: NEW-07 same-block overspend (200 of a token, balance 191)
            ["b5b99795d923a7a6849db4562ebaa2419b29a058a14dbbadb23c7486b17027fc"] = 5_662_203, // TKNZ_WD_ARB: lead-arbiter rule (TXHeightRule5) needs arbiter signing addresses a node fetches over HTTP; arbiters are retired (NEW-28)
        };

        private static readonly IReadOnlyDictionary<string, long> Testnet = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["b9c2b93e8f59837a40555010d8c25f698b83996696932d9a4201a43e76e72765"] = 64_607, // VBTC_V2_BRIDGE_EXIT_TO_BTC: b61d49f5 committee-caster submitter
            ["2ba20f2b99faeaca472bd3ff91528befac8880130a16da5dd5332997a729cfec"] = 64_609, // VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE: b61d49f5 committee-caster submitter
        };

        public static int MainnetCount => Mainnet.Count;
        public static int TestnetCount => Testnet.Count;

        // ── Reserve reversals the network applied the pre-6b914353 way (owner decision, 26 Sep 2026) ────────────────

        private sealed record ReserveReversal(long Height, string From, string Function, string? CalledBackHash);

        /// <summary>
        /// Before 6b914353 (2026-08-30) CallBack() and Recover() refunded the reserve sender on every node but set the
        /// transfer's reserve row to CalledBack / Recovered only on a node whose LOCAL WALLET held the transaction. Every
        /// other node left the row Pending, and 24 h later the finalizer paid the recipient as well: 11 mainnet transfers,
        /// 299,615.99 VFX paid twice, all by 5,519,286. The fix flips the row on every node, with no activation height, so
        /// a node syncing from genesis computed lower balances than the live network and refused block 4,124,088 (a
        /// recipient spending its second payment). For exactly these control transactions the row is left Pending, as the
        /// network applied them; every other CallBack()/Recover() is unchanged. Bound to hash, block height, sender,
        /// function and (for CallBack) the transfer called back - five of these 2023 transactions do not recompute to
        /// their stored hash (decimal scale lost in storage), and nothing else in them is read.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, ReserveReversal> MainnetReserveReversals = new Dictionary<string, ReserveReversal>(StringComparer.Ordinal)
        {
            // Recover() - the two pending transfers to REjHm... (995.99 + 8,998.99)
            ["2f20dff8b6264688179edb8bf911fef22647643ae3dda4b7983b5f780568c7ed"] = new(1_243_110, "xRBX9Zgi3Dsyufu8xG4Yf1W95gdhXa1ApZ", "Recover()", null),
            // CallBack() - transfer called back (amount, recipient)
            ["e86c39058693c6a564a5afe21a5e2b6128f623a7a1cfd652e862133be2b968bb"] = new(1_659_325, "xRBX68MKPk1rqXvB7jzsRW4BSTZCkkvHh7", "CallBack()", "bc76703f48f0e330c01001cc9abf5e2b1a76120b04aec2fce3e287225ed7542a"), // 4,000 to RF3X6EB...
            ["63bed6dbe633ef339be99056b3405f34c0932c559e56f3ea8e2226e0d3c482b4"] = new(1_659_327, "xRBX68MKPk1rqXvB7jzsRW4BSTZCkkvHh7", "CallBack()", "b033369360f0484ac3e63cc6b484888964d1d9888aab8b3ea3c8da9ce009e691"), // 1,500 to RBFKT...
            ["7f285e062d6dcb355b9b13e1c90fb5ebf48c9fd81667376f18c70949038e6d79"] = new(1_659_333, "xRBX68MKPk1rqXvB7jzsRW4BSTZCkkvHh7", "CallBack()", "76f22da053567c6c7a7f363219bf6092f1fb1277d4d4bb0d40df1632e9f70afa"), // 3,500 to RBEE9...
            ["a115b5d34c58bc61c5284de04bb163c4a39b9a803f31949cd9598ada889835eb"] = new(1_659_381, "xRBX68MKPk1rqXvB7jzsRW4BSTZCkkvHh7", "CallBack()", "52f29b16076136c7edb2f37ac2c4fa8115af795fe0d41908ee7ada0f1f6314e5"), // 3,500 to RBEE9...
            ["d6c8c9d87c08a4f3724802dd80aacc88db83a038ae6a259834131fa6ec077fd4"] = new(3_821_832, "xRBXapXc1PGevze59V7Wc4Gb5pK32ShvT9", "CallBack()", "cefdefdd32939e85bcb6ef5ef8f8c55f68293a515ba2d55321e3abbdba6826e5"), // 10 to RQKDU...
            ["9a1fd70f1735aa69c19e91aea2d27b0a121e7cf4acf512b9896c3d50554ca15b"] = new(3_944_533, "xRBXYfe8TNEmptPriAehPpvPTdBQgVhebP", "CallBack()", "60af58affb14a79b322677870949dbc27636c4f0e89cc7c2afae94a540b85f3e"), // 10 to RC8Jd...
            ["f7aacfb052b9782619c24e3f4809aa025dd020f792224f93bfdec6cf04a79ac6"] = new(5_099_581, "xRBX4G1QWZFjc6srKvxYeAYFsLkdC68d1z", "CallBack()", "c172b606c36eb8d79265204de8d4f6fde3c82d00e84745b60741bd17849e76c2"), // 1 to RWwag...
            ["f82d35452b020bb135274201dc82b0727c2157468b87e1f33c20688289b5b52b"] = new(5_351_562, "xRBXYfe8TNEmptPriAehPpvPTdBQgVhebP", "CallBack()", "89ef3abae49ca2270cc6b9fc055befc27f923869bd5a3b195f86d0adc54c41b2"), // 277,100 to RC9YF...
            ["6238a4d64a3ae255ee05b0fb177bfa5915e76bf2dc774004d376a4faba461b56"] = new(5_519_286, "xRBXTWkCBdbzJRrETU8ZMYRJBPzWuzx2Ei", "CallBack()", "bcb8f5ca2c36ce610f96215decb8840f2a4bad428ad54d602a8e4ce80ed46fba"), // 0.01 to RW6fa...
        };

        public static int MainnetReserveReversalCount => MainnetReserveReversals.Count;

        /// <summary>Whether this mined CallBack()/Recover() leaves its transfers Pending, as the network applied it.</summary>
        public static bool KeepsReversedTransferPending(Transaction? tx, long? blockHeight)
        {
            if (Globals.IsTestNet || tx == null || blockHeight == null || string.IsNullOrEmpty(tx.Hash) || string.IsNullOrEmpty(tx.Data)) return false;
            if (!MainnetReserveReversals.TryGetValue(tx.Hash, out var r)) return false;
            if (r.Height != blockHeight.Value || tx.TransactionType != TransactionType.RESERVE || !string.Equals(tx.FromAddress, r.From, StringComparison.Ordinal))
                return false;
            try
            {
                var data = Newtonsoft.Json.Linq.JObject.Parse(tx.Data);
                return (string?)data["Function"] == r.Function && (r.CalledBackHash == null || (string?)data["Hash"] == r.CalledBackHash);
            }
            catch { return false; }
        }

        /// <summary>Whether this mined transaction is on the list for this block height (block validation only).</summary>
        public static bool IsAccepted(Transaction? tx, long? blockHeight)
        {
            if (tx == null || blockHeight == null || string.IsNullOrEmpty(tx.Hash)) return false;
            var list = Globals.IsTestNet ? Testnet : Mainnet;
            if (!list.TryGetValue(tx.Hash, out var height) || height != blockHeight.Value) return false;
            try { return tx.GetHash() == tx.Hash; } catch { return false; }
        }
    }
}
