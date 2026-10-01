using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Mainnet report (2026-10-01): a web wallet withdrawal failed with "NativeAggregationFailed — native SignAggregate
    /// returned error code -4 (validators=158/108/161)". The vault's DKG had 162 participants; one was no longer
    /// reachable, so the ceremony ran over 161. Validators sign with their DKG identifiers (stored participant order),
    /// but Spyglass's node coordinates web wallet withdrawals without a key package for the vault, so it numbered the
    /// 161 reachable signers instead and every identifier after the missing participant was off by one.
    /// </summary>
    public class FrostAggregateParticipantOrderTests
    {
        private static readonly List<string> DkgParticipants = new() { "xAlpha", "xBravo", "xCharlie", "xDelta", "xEcho" };
        private const ushort MinSigners = 3;
        private const string MessageHash = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

        private static string Id(int i) => i.ToString("x").PadLeft(64, '0');

        /// <summary>A real DKG over <see cref="DkgParticipants"/>: identifier i belongs to the i-th address in sorted order.</summary>
        private static (Dictionary<string, string> KeyPackages, string PubkeyPackage) RealDkg()
        {
            var sorted = DkgParticipants.OrderBy(a => a, System.StringComparer.Ordinal).ToList();
            int n = sorted.Count;
            var commitments = new string[n + 1];
            var round1Secrets = new string[n + 1];
            for (ushort i = 1; i <= n; i++)
            {
                var (c, s, e) = FrostNative.DKGRound1Generate(i, (ushort)n, MinSigners);
                Assert.Equal(FrostNative.SUCCESS, e);
                (commitments[i], round1Secrets[i]) = (c, s);
            }

            var sharesByDealer = new Dictionary<int, JObject>();
            var round2Secrets = new string[n + 1];
            for (int dealer = 1; dealer <= n; dealer++)
            {
                var others = new JObject();
                for (int j = 1; j <= n; j++) if (j != dealer) others[Id(j)] = JToken.Parse(commitments[j]);
                var (sh, secret, e) = FrostNative.DKGRound2GenerateShares(round1Secrets[dealer], others.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(FrostNative.SUCCESS, e);
                sharesByDealer[dealer] = JObject.Parse(sh);
                round2Secrets[dealer] = secret;
            }

            var keyPackages = new Dictionary<string, string>();
            string pubkeyPackage = "";
            for (int i = 1; i <= n; i++)
            {
                var round1 = new JObject();
                var round2 = new JObject();
                for (int j = 1; j <= n; j++)
                {
                    if (j == i) continue;
                    round1[Id(j)] = JToken.Parse(commitments[j]);
                    round2[Id(j)] = sharesByDealer[j][Id(i)];
                }
                var (_, keyPackage, pkp, e) = FrostNative.DKGRound3Finalize(
                    round2Secrets[i], round1.ToString(Newtonsoft.Json.Formatting.None), round2.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(FrostNative.SUCCESS, e);
                keyPackages[sorted[i - 1]] = keyPackage;
                pubkeyPackage = pkp;
            }
            return (keyPackages, pubkeyPackage);
        }

        /// <summary>
        /// Signs with <paramref name="signers"/> the way validators do (each with its DKG identifier), then aggregates
        /// with the coordinator's identifier map built from <paramref name="coordinatorOrder"/>.
        /// </summary>
        private static int SignAndAggregate(List<string> signers, List<string> coordinatorOrder)
        {
            var (keyPackages, pubkeyPackage) = RealDkg();
            var dkgIds = FrostMPCService.BuildAddressToFrostIdentifierMap(DkgParticipants);

            var nonceSecrets = new Dictionary<string, string>();
            var commitmentsByAddress = new Dictionary<string, string>();
            foreach (var signer in signers)
            {
                var (commitment, secret, e) = FrostNative.SignRound1Nonces(keyPackages[signer]);
                Assert.Equal(FrostNative.SUCCESS, e);
                (commitmentsByAddress[signer], nonceSecrets[signer]) = (commitment, secret);
            }

            var validatorView = new JObject();
            foreach (var signer in signers) validatorView[dkgIds[signer]] = JToken.Parse(commitmentsByAddress[signer]);
            var sharesByAddress = new Dictionary<string, string>();
            foreach (var signer in signers)
            {
                var (share, e) = FrostNative.SignRound2Signature(
                    keyPackages[signer], nonceSecrets[signer], validatorView.ToString(Newtonsoft.Json.Formatting.None), MessageHash);
                Assert.Equal(FrostNative.SUCCESS, e);
                sharesByAddress[signer] = share;
            }

            var coordinatorIds = FrostMPCService.BuildAddressToFrostIdentifierMap(coordinatorOrder);
            var shares = new JObject();
            var nonces = new JObject();
            foreach (var signer in signers)
            {
                shares[coordinatorIds[signer]] = JToken.Parse(sharesByAddress[signer]);
                nonces[coordinatorIds[signer]] = JToken.Parse(commitmentsByAddress[signer]);
            }
            var (_, code) = FrostNative.SignAggregate(
                shares.ToString(Newtonsoft.Json.Formatting.None), nonces.ToString(Newtonsoft.Json.Formatting.None), MessageHash, pubkeyPackage);
            return code;
        }

        private static readonly List<string> SignersWithoutBravo = new() { "xAlpha", "xCharlie", "xDelta", "xEcho" };

        [Fact]
        public void NumberingTheReachableSigners_FailsWhenAParticipantIsMissing()
        {
            Assert.Equal(FrostNative.ERROR_CRYPTO_ERROR, SignAndAggregate(SignersWithoutBravo, SignersWithoutBravo));
        }

        [Fact]
        public void NumberingTheDkgParticipants_Aggregates()
        {
            Assert.Equal(FrostNative.SUCCESS, SignAndAggregate(SignersWithoutBravo, DkgParticipants));
        }

        [Fact]
        public void ResolveOrder_PrefersTheStoredDkgOrder()
        {
            var stored = new List<string> { "s1", "s2" };
            var order = FrostMPCService.ResolveAggregationOrder(stored, DkgParticipants, "{}", SignersWithoutBravo, out var source);
            Assert.Same(stored, order);
            Assert.Equal("stored DKG order", source);
        }

        [Fact]
        public void ResolveOrder_UsesTheContractParticipants_WhenTheyMatchThePubkeyPackage()
        {
            var (_, pubkeyPackage) = RealDkg();
            Assert.Equal(DkgParticipants.Count, FrostMPCService.CountVerifyingShares(pubkeyPackage));

            var order = FrostMPCService.ResolveAggregationOrder(null, DkgParticipants, pubkeyPackage, SignersWithoutBravo, out var source);
            Assert.Same(DkgParticipants, order);
            Assert.Equal("contract DKG participants", source);
        }

        [Fact]
        public void ResolveOrder_KeepsTheSigners_WhenTheContractListDoesNotMatchTheDkg()
        {
            var (_, pubkeyPackage) = RealDkg();
            var snapshotWithExtra = DkgParticipants.Append("xFoxtrot").ToList();

            var order = FrostMPCService.ResolveAggregationOrder(null, snapshotWithExtra, pubkeyPackage, SignersWithoutBravo, out var source);
            Assert.Same(SignersWithoutBravo, order);
            Assert.StartsWith("signer addresses", source);
        }

        [Fact]
        public void ResolveOrder_KeepsTheSigners_WhenNoParticipantListIsFound()
        {
            var order = FrostMPCService.ResolveAggregationOrder(null, null, "{}", SignersWithoutBravo, out var source);
            Assert.Same(SignersWithoutBravo, order);
            Assert.StartsWith("signer addresses", source);
        }

        [Fact]
        public void CountVerifyingShares_IsNullForAnUnreadablePackage()
        {
            Assert.Null(FrostMPCService.CountVerifyingShares("not json"));
        }
    }
}
