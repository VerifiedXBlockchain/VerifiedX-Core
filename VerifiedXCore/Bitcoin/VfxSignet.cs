using NBitcoin;

namespace VerifiedXCore.Bitcoin
{
    /// <summary>
    /// The Bitcoin chain the VFX testnet follows: a private signet (BIP 325) whose blocks only the VerifiedX node farm
    /// can sign. It keeps mainnet's 10-minute blocks and retargeting, at the easiest target a signet allows, and uses
    /// testnet address formats (tb1, 2, m/n). The network itself (challenge, message start c69e715c, genesis) is in the
    /// bundled NBitcoin; this is where the VFX testnet finds it.
    /// </summary>
    public static class VfxSignet
    {
        public static Network Network => Network.VfxSignet;

        /// <summary>The signet's bitcoinds, each also serving Electrum over TLS (<see cref="ElectrumTlsPort"/>).</summary>
        public static readonly string[] Hosts = { "signet.verifiedx.io", "signet-2.verifiedx.io" };

        public const int ElectrumTlsPort = 50002;

        /// <summary>The signet's Esplora API (a mempool.space instance run with the chain).</summary>
        public const string EsploraUrl = "https://signet.verifiedx.io/api";
    }
}
