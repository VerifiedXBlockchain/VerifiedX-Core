using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.FROST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-19 (found by the sixth independent review; CRITICAL, pre-existing): FROST DKG round-2 packages carry each
    /// recipient's secret share in plaintext ("signing_share"), and the round-2 endpoint returned all of them to the
    /// coordinator (any wallet starting a vBTC V2 contract DKG) over plain HTTP, which redistributed them. With a
    /// threshold below the participant count the coordinator could interpolate every dealer's polynomial and so the
    /// vault key. Packages are now sealed to each recipient's validator key; the coordinator relays ciphertext.
    /// </summary>
    public class NEW19_FrostShareSealingTests
    {
        private static string Id(int i) => i.ToString("x").PadLeft(64, '0');

        private static (NBitcoin.Key Key, string PubHex) NewValidatorKey()
        {
            var k = new NBitcoin.Key();
            return (k, Convert.ToHexString(k.PubKey.Decompress().ToBytes()).ToLowerInvariant());
        }

        [Fact]
        public void NEW19_PoC_NativeRound2OutputCarriesPlaintextSecretShares()
        {
            var (c2, _, _) = FrostNative.DKGRound1Generate(2, 3, 2);
            var (c3, _, _) = FrostNative.DKGRound1Generate(3, 3, 2);
            var (_, s1, _) = FrostNative.DKGRound1Generate(1, 3, 2);
            var others = new JObject { [Id(2)] = JToken.Parse(c2), [Id(3)] = JToken.Parse(c3) };
            var (shares, _, err) = FrostNative.DKGRound2GenerateShares(s1, others.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(FrostNative.SUCCESS, err);
            Assert.Contains("signing_share", shares); // what the coordinator used to receive for every recipient
        }

        [Fact]
        public void NEW19_SealedDkg_CoordinatorSeesNoShares_AndEveryParticipantStillFinalizesTheSameKey()
        {
            const string session = "sess-new19";
            var keys = Enumerable.Range(0, 4).Select(_ => NewValidatorKey()).ToArray(); // index 1..3 used
            var r1 = new (string Commitment, string Secret)[4];
            for (ushort i = 1; i <= 3; i++)
            {
                var (c, s, e) = FrostNative.DKGRound1Generate(i, 3, 2);
                Assert.Equal(FrostNative.SUCCESS, e);
                r1[i] = (c, s);
            }

            // Each dealer runs round 2 and seals its packages (what the coordinator now receives).
            var idToAddr = Enumerable.Range(1, 3).ToDictionary(i => Id(i), i => $"addr{i}");
            var round2Secret = new string[4];
            var sealedByDealer = new Dictionary<int, string>();
            for (int dealer = 1; dealer <= 3; dealer++)
            {
                var others = new JObject();
                for (int j = 1; j <= 3; j++) if (j != dealer) others[Id(j)] = JToken.Parse(r1[j].Commitment);
                var (shares, secret, err) = FrostNative.DKGRound2GenerateShares(r1[dealer].Secret, others.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(FrostNative.SUCCESS, err);
                round2Secret[dealer] = secret;
                var sealedShares = FrostShareCrypto.SealAll(shares, session, idToAddr, a => keys[int.Parse(a.Substring(4))].PubHex, out var sealErr);
                Assert.Null(sealErr);
                Assert.DoesNotContain("signing_share", sealedShares);                            // coordinator's view
                sealedByDealer[dealer] = sealedShares!;
            }

            // Each recipient opens its own packages and finalizes.
            var groupKeys = new HashSet<string>();
            for (int me = 1; me <= 3; me++)
            {
                var myPriv = keys[me].Key.ToBytes();
                var received = new JObject();
                var round1Others = new JObject();
                for (int dealer = 1; dealer <= 3; dealer++)
                {
                    if (dealer == me) continue;
                    var token = (string?)JObject.Parse(sealedByDealer[dealer])[Id(me)];
                    Assert.True(FrostShareCrypto.TryOpen(token, myPriv, session, Id(me), out var package));
                    received[Id(dealer)] = JToken.Parse(package);
                    round1Others[Id(dealer)] = JToken.Parse(r1[dealer].Commitment);

                    // Nobody else can open it, and it is bound to this session and recipient.
                    Assert.False(FrostShareCrypto.TryOpen(token, keys[dealer].Key.ToBytes(), session, Id(me), out _));
                    Assert.False(FrostShareCrypto.TryOpen(token, myPriv, "other-session", Id(me), out _));
                }
                var (groupPubkey, _, _, finErr) = FrostNative.DKGRound3Finalize(round2Secret[me],
                    round1Others.ToString(Newtonsoft.Json.Formatting.None), received.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(FrostNative.SUCCESS, finErr);
                groupKeys.Add(groupPubkey);
            }
            Assert.Single(groupKeys); // all three derived the same group key
        }

        [Fact]
        public void NEW19_PlaintextPackageIsRefused()
        {
            var me = NewValidatorKey();
            Assert.False(FrostShareCrypto.TryOpen("{\"signing_share\":\"00\"}", me.Key.ToBytes(), "s", Id(1), out _));
        }

        [Fact]
        public void NEW19_Round2EndpointReleasesOnlySealedShares()
        {
            // No live FROST host in the test suite: pin the endpoint wiring.
            var repo = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(ThisFile()))!;
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, "VerifiedXCore", "Bitcoin", "FROST", "FrostStartup.cs"));
            Assert.Contains("GeneratedShares = sealedShares", src);
            Assert.DoesNotContain("GeneratedShares = sharesJson", src);
            Assert.DoesNotContain("session.ReceivedSharesJson.TryAdd(share.FromValidatorAddress, share.EncryptedShare)", src);
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
