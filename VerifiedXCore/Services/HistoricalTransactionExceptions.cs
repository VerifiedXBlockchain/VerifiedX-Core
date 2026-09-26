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
