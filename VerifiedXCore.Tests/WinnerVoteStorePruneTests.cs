using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The winner-vote route runs on every validator, but the votes it keeps were pruned only by the caster agreement
    /// loop. A validator that is not a caster kept every vote a caster sent it, at every height, for good. The store now
    /// prunes what is more than <see cref="WinnerVoteStore.KeepHeightsBehindTip"/> heights behind the tip whenever it
    /// keeps a vote, in the signed votes and in both maps it writes.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class WinnerVoteStorePruneTests : IDisposable
    {
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, string>> _priorVotes = Globals.CasterWinnerVoteDict;
        private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, List<string>>> _priorExcluded = Globals.CasterExcludedAddressDict;

        private readonly (PrivateKey Key, string Pub, string Address) _caster = NewKey();

        public WinnerVoteStorePruneTests()
        {
            // This node is a validator that is not a caster: it never runs the agreement loop.
            Globals.ValidatorAddress = "";
            Globals.BlockCasters = new ConcurrentBag<Peers> { new Peers { ValidatorAddress = _caster.Address, PeerIP = "10.0.0.7" } };
            Globals.CasterWinnerVoteDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, string>>();
            Globals.CasterExcludedAddressDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, List<string>>>();
            Globals.BannedIPs ??= new ConcurrentDictionary<string, Peers>();
        }

        public void Dispose()
        {
            WinnerVoteStore.PruneBelow(long.MaxValue);
            Globals.LastBlock = _priorLastBlock;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorValidator;
            Globals.CasterWinnerVoteDict = _priorVotes;
            Globals.CasterExcludedAddressDict = _priorExcluded;
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private ActionResult<string> Vote(long tip, long height)
        {
            Globals.LastBlock = new Block { Height = tip };
            var req = new WinnerVoteRequest
            {
                BlockHeight = height, VoterAddress = _caster.Address, WinnerAddress = "xWINNER", Sequence = height,
                ExcludedAddresses = new List<string> { "xEXCLUDED" },
            };
            req.Signature = SignatureService.CreateSignature(req.ToSignedVote().SigningMessage(), _caster.Key, _caster.Pub);
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("172.28.0.20");
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = ctx } }.ExchangeWinnerVote(req);
        }

        [Fact]
        public void A_non_caster_drops_votes_far_behind_the_tip_when_it_keeps_a_new_one()
        {
            Assert.IsType<OkObjectResult>(Vote(tip: 6, height: 7).Result);
            Assert.True(Globals.CasterWinnerVoteDict.ContainsKey(7));
            Assert.True(Globals.CasterExcludedAddressDict.ContainsKey(7));
            Assert.Single(WinnerVoteStore.GetSigned(7));

            // The chain moves on; the next round's vote arrives.
            Assert.IsType<OkObjectResult>(Vote(tip: 7 + WinnerVoteStore.KeepHeightsBehindTip + 1, height: 7 + WinnerVoteStore.KeepHeightsBehindTip + 2).Result);

            Assert.False(Globals.CasterWinnerVoteDict.ContainsKey(7));
            Assert.False(Globals.CasterExcludedAddressDict.ContainsKey(7));
            Assert.Empty(WinnerVoteStore.GetSigned(7));
            Assert.True(Globals.CasterWinnerVoteDict.ContainsKey(7 + WinnerVoteStore.KeepHeightsBehindTip + 2));
        }

        [Fact]
        public void Votes_within_the_kept_window_stay()
        {
            Vote(tip: 6, height: 7);
            Vote(tip: 7 + WinnerVoteStore.KeepHeightsBehindTip, height: 8 + WinnerVoteStore.KeepHeightsBehindTip);

            // Height 7 is exactly KeepHeightsBehindTip behind the tip: still kept.
            Assert.True(Globals.CasterWinnerVoteDict.ContainsKey(7));
            Assert.Single(WinnerVoteStore.GetSigned(7));
        }
    }
}
