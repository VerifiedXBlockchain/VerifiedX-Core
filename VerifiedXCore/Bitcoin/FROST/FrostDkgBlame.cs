using System.Numerics;
using NBitcoin.Secp256k1;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// Attributing a failed vBTC key ceremony (DKG) to the participant that caused it, so the coordinator can drop that
    /// participant and run the ceremony again instead of failing outright. Mainnet report: 3 of 162 validators never
    /// finished, the contract needs an attestation from every participant, and any 3 nodes could block vault creation.
    /// The native library reports every DKG crypto failure as one opaque code, so the checks that identify a culprit
    /// are done here:
    ///  - a Round 1 package's proof of knowledge is checked by the coordinator through the native library;
    ///  - a Round 2 share is checked by its recipient against the sender's public commitment (Feldman VSS);
    ///  - recipients' reports are resolved so one dishonest participant cannot exclude many honest ones.
    /// </summary>
    public static class FrostDkgBlame
    {
        public const string AccusationUnopenable = "Unopenable";
        public const string AccusationInvalidShare = "InvalidShare";

        // secp256k1 group order.
        private static readonly BigInteger Order = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);

        /// <summary>
        /// Feldman check of one Round 2 share: signing_share·G == Σ_k x^k·C_k, where C_k are the sender's Round 1
        /// commitment coefficients and x is the recipient's identifier (the same check FROST part3 makes, which the native
        /// library reports without naming the sender). False when the share or commitment cannot be read.
        /// </summary>
        public static bool ShareMatchesCommitment(string round2PackageJson, string round1PackageJson, string recipientIdentifierHex)
        {
            try
            {
                var share = Convert.FromHexString((string?)JObject.Parse(round2PackageJson)["signing_share"] ?? "");
                if (share.Length != 32 || !ECPrivKey.TryCreate(share, out var shareKey) || shareKey == null)
                    return false;
                var lhs = shareKey.CreatePubKey();

                var coefficients = JObject.Parse(round1PackageJson)["commitment"] as JArray;
                if (coefficients == null || coefficients.Count == 0)
                    return false;

                var x = new BigInteger(Convert.FromHexString(recipientIdentifierHex), isUnsigned: true, isBigEndian: true) % Order;
                if (x.IsZero)
                    return false;

                var terms = new List<ECPubKey>();
                var power = BigInteger.One;
                foreach (var c in coefficients)
                {
                    if (!ECPubKey.TryCreate(Convert.FromHexString((string?)c ?? ""), Context.Instance, out _, out var point) || point == null)
                        return false;
                    terms.Add(power.IsOne ? point : point.TweakMul(Scalar32(power)));
                    power = power * x % Order;
                }
                if (!ECPubKey.TryCombine(Context.Instance, terms.ToArray(), out var rhs) || rhs == null)
                    return false;

                return lhs.ToBytes(true).AsSpan().SequenceEqual(rhs.ToBytes(true));
            }
            catch
            {
                return false;
            }
        }

        private static byte[] Scalar32(BigInteger value)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            var padded = new byte[32];
            Buffer.BlockCopy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
            return padded;
        }

        /// <summary>
        /// The participants whose Round 1 package is unreadable or carries an invalid proof of knowledge. Every honest
        /// participant's Round 2 (FROST part2) fails on such a package, so without this one bad commitment would look
        /// like every other participant failing. Checked with the native library: a stand-in participant runs part2 over
        /// the packages (part2 verifies each proof under the package's identifier). One call covers the common case of
        /// no bad package; otherwise each package is checked alone, padded with valid stand-in packages to the size part2
        /// requires. Null when the native library is unavailable.
        /// </summary>
        public static HashSet<string>? InvalidRound1Packages(IReadOnlyDictionary<string, string> packageByAddress, List<string> participants, int minSigners)
        {
            try
            {
                var idByAddress = FrostStartup.BuildAddressToIdentifierMap(participants);
                var invalid = new HashSet<string>(StringComparer.Ordinal);
                var parsed = new Dictionary<string, JToken>(StringComparer.Ordinal);
                foreach (var (address, package) in packageByAddress)
                {
                    try
                    {
                        if (!idByAddress.ContainsKey(address)) { invalid.Add(address); continue; }
                        parsed[address] = JToken.Parse(package);
                    }
                    catch { invalid.Add(address); }
                }
                if (parsed.Count == 0 || minSigners < 2)
                    return invalid;

                // Stand-in identifiers sit far above any participant index (participants are at most 512).
                const ushort standInBase = 60000;

                if (parsed.Count + 1 >= minSigners && Part2Accepts(parsed.ToDictionary(p => idByAddress[p.Key], p => p.Value), standInBase, (ushort)minSigners) == true)
                    return invalid;

                var fillers = new Dictionary<string, JToken>();
                for (int i = 1; i <= minSigners - 2; i++)
                {
                    var (commitment, _, err) = FrostNative.DKGRound1Generate((ushort)(standInBase + i), (ushort)minSigners, (ushort)minSigners);
                    if (err != FrostNative.SUCCESS) return null;
                    fillers[ParticipantIdentifier(standInBase + i)] = JToken.Parse(commitment);
                }
                foreach (var (address, package) in parsed)
                {
                    var map = new Dictionary<string, JToken>(fillers) { [idByAddress[address]] = package };
                    var accepted = Part2Accepts(map, standInBase, (ushort)minSigners);
                    if (accepted == null) return null;
                    if (accepted == false) invalid.Add(address);
                }
                return invalid;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Round 1 package check failed: {ex.Message}", "FrostDkgBlame.InvalidRound1Packages");
                return null;
            }
        }

        /// <summary>Runs FROST part2 for a stand-in participant over these packages; null when the library is unusable.</summary>
        private static bool? Part2Accepts(Dictionary<string, JToken> packagesByIdentifier, ushort standInId, ushort minSigners)
        {
            var maxSigners = (ushort)(packagesByIdentifier.Count + 1);
            var (_, secret, err) = FrostNative.DKGRound1Generate(standInId, maxSigners, minSigners);
            if (err != FrostNative.SUCCESS) return null;
            var map = new JObject();
            foreach (var (id, package) in packagesByIdentifier) map[id] = package;
            var (_, _, part2Err) = FrostNative.DKGRound2GenerateShares(secret, map.ToString(Newtonsoft.Json.Formatting.None));
            return part2Err == FrostNative.SUCCESS;
        }

        private static string ParticipantIdentifier(int index) => index.ToString("x").PadLeft(64, '0');

        /// <summary>
        /// Who to exclude, given recipients' reports that a sender's share would not open or did not match its commitment.
        /// A report cannot be proven to a third party (the share is sealed to the recipient), so the rule bounds the damage
        /// instead: repeatedly drop whoever is involved in the most open reports - a sender accused by many recipients, or
        /// a recipient accusing many senders (e.g. one that cannot open any share because its own key is unavailable).
        /// When the remaining reports are one-to-one, both sides are dropped: an honest participant can then only be
        /// removed together with a participant that lied, so an attacker loses one node for every honest one it removes.
        /// </summary>
        public static HashSet<string> ResolveAccusations(IEnumerable<(string Accuser, string Accused)> accusations)
        {
            var open = accusations.Where(a => a.Accuser != a.Accused).Distinct().ToList();
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            while (open.Count > 0)
            {
                var degree = open.SelectMany(a => new[] { a.Accuser, a.Accused })
                    .GroupBy(n => n, StringComparer.Ordinal)
                    .Select(g => (Node: g.Key, Count: g.Count()))
                    .OrderByDescending(n => n.Count).ThenBy(n => n.Node, StringComparer.Ordinal)
                    .ToList();
                if (degree[0].Count > 1)
                {
                    excluded.Add(degree[0].Node);
                }
                else
                {
                    foreach (var a in open) { excluded.Add(a.Accuser); excluded.Add(a.Accused); }
                }
                open = open.Where(a => !excluded.Contains(a.Accuser) && !excluded.Contains(a.Accused)).ToList();
            }
            return excluded;
        }
    }
}
