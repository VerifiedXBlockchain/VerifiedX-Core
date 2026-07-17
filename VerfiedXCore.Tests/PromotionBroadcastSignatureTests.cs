using System.Collections.Concurrent;
using ReserveBlockCore;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// CONSENSUS-V2 (Fix #3): Promotion announcement guardrails.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class PromotionBroadcastSignatureTests
    {
        [Fact]
        public async Task HandlePromotionAnnouncement_UntrustedPromoter_IsRejected()
        {
            var originalCasters = Globals.BlockCasters;
            var originalKnownCasters = Globals.KnownCasters.ToList();
            var originalHeight = Globals.LastBlock.Height;

            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                lock (Globals.KnownCastersLock)
                {
                    Globals.KnownCasters.Clear();
                }
                Globals.LastBlock.Height = 1000;

                var request = new CasterPromotionAnnouncement
                {
                    PromotedAddress = "xNEW",
                    PromotedIP = "127.0.0.1",
                    PromotedPublicKey = "NEWPK",
                    PromotedWalletVersion = "beta6.0.6",
                    BlockHeight = 1000,
                    PromoterAddress = "xUNTRUSTED",
                    PromoterSignature = "invalid",
                };

                var result = await CasterDiscoveryService.HandlePromotionAnnouncement(request);

                Assert.Contains("untrusted promoter", result, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Globals.LastBlock.Height = originalHeight;
                Globals.BlockCasters = originalCasters;
                lock (Globals.KnownCastersLock)
                {
                    Globals.KnownCasters.Clear();
                    foreach (var kc in originalKnownCasters)
                        Globals.KnownCasters.Add(kc);
                }
            }
        }

        [Fact]
        public async Task HandlePromotionAnnouncement_BadSignature_IsRejected()
        {
            var originalCasters = Globals.BlockCasters;
            var originalKnownCasters = Globals.KnownCasters.ToList();
            var originalHeight = Globals.LastBlock.Height;

            try
            {
                Globals.LastBlock.Height = 1000;
                Globals.BlockCasters = new ConcurrentBag<Peers>(new[]
                {
                    new Peers
                    {
                        ValidatorAddress = "xPROMOTER",
                        PeerIP = "127.0.0.1",
                        IsValidator = true,
                        ValidatorPublicKey = "PROMOTER_PK"
                    }
                });
                lock (Globals.KnownCastersLock)
                {
                    Globals.KnownCasters.Clear();
                    Globals.KnownCasters.Add(new CasterInfo
                    {
                        Address = "xPROMOTER",
                        PeerIP = "127.0.0.1",
                        PublicKey = "PROMOTER_PK"
                    });
                }

                var request = new CasterPromotionAnnouncement
                {
                    PromotedAddress = "xNEW",
                    PromotedIP = "127.0.0.1",
                    PromotedPublicKey = "NEWPK",
                    PromotedWalletVersion = "beta6.0.6",
                    BlockHeight = 1000,
                    PromoterAddress = "xPROMOTER",
                    PromoterSignature = "bad_signature",
                };

                var result = await CasterDiscoveryService.HandlePromotionAnnouncement(request);

                Assert.Contains("bad signature", result, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Globals.LastBlock.Height = originalHeight;
                Globals.BlockCasters = originalCasters;
                lock (Globals.KnownCastersLock)
                {
                    Globals.KnownCasters.Clear();
                    foreach (var kc in originalKnownCasters)
                        Globals.KnownCasters.Add(kc);
                }
            }
        }
    }
}
