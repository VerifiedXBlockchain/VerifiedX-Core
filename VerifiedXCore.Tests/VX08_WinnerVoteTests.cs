using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-08 (HIGH): "Winner votes are accepted unsigned, overwritten, and pre-seeded for future heights".
    ///
    /// Audit PoC: request A stored caster X's vote for the honest winner at height 7; request B, naming the
    /// same public caster address, overwrote it with the attacker's winner; a vote for height 9999 was
    /// accepted at tip 0. Control: a non-caster voter got 400 and nothing stored. Readback proved the vote was
    /// stored server-side.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class VX08_WinnerVoteTests : IDisposable
    {
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, string>> _priorVotes = Globals.CasterWinnerVoteDict;

        private readonly (PrivateKey Key, string Pub, string Address) _casterX = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _outsider = NewKey();

        public VX08_WinnerVoteTests()
        {
            Globals.LastBlock = new Block { Height = 6 };
            Globals.ValidatorAddress = "";
            Globals.BlockCasters = new ConcurrentBag<Peers> { new Peers { ValidatorAddress = _casterX.Address, PeerIP = "10.0.0.7" } };
            Globals.CasterWinnerVoteDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, string>>();
            Globals.BannedIPs ??= new ConcurrentDictionary<string, Peers>();
        }

        public void Dispose()
        {
            WinnerVoteStore.PruneBelow(long.MaxValue);
            Globals.LastBlock = _priorLastBlock;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorValidator;
            Globals.CasterWinnerVoteDict = _priorVotes;
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static WinnerVoteRequest Signed((PrivateKey Key, string Pub, string Address) voter, long height, string winner, long seq)
        {
            var req = new WinnerVoteRequest { BlockHeight = height, VoterAddress = voter.Address, WinnerAddress = winner, Sequence = seq };
            req.Signature = SignatureService.CreateSignature(req.ToSignedVote().SigningMessage(), voter.Key, voter.Pub);
            return req;
        }

        private static ValidatorController Controller()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("172.28.0.20");
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = ctx } };
        }

        private string? StoredVote(long h) =>
            Globals.CasterWinnerVoteDict.TryGetValue(h, out var d) && d.TryGetValue(_casterX.Address, out var w) ? w : null;

        [Fact]
        public void VX08_AuditPoC_UnsignedOverwrite_Refused_HonestVoteKept()
        {
            var a = Controller().ExchangeWinnerVote(Signed(_casterX, 7, "xVICTIM_HONEST_WINNER", 1));
            Assert.IsType<OkObjectResult>(a.Result);

            // Request B: the audit's forgery — the caster's public address, attacker's winner, no signature.
            var forged = new WinnerVoteRequest { BlockHeight = 7, VoterAddress = _casterX.Address, WinnerAddress = "xATTACKER_WINNER" };
            var b = Controller().ExchangeWinnerVote(forged);

            Assert.IsType<BadRequestObjectResult>(b.Result);
            Assert.Equal("xVICTIM_HONEST_WINNER", StoredVote(7));
        }

        [Fact]
        public void VX08_ReplayOfAnOlderSignedVote_DoesNotReplaceTheNewerOne()
        {
            Controller().ExchangeWinnerVote(Signed(_casterX, 7, "xFIRST", 1));
            Controller().ExchangeWinnerVote(Signed(_casterX, 7, "xSECOND", 2)); // caster's own re-vote on retry
            Controller().ExchangeWinnerVote(Signed(_casterX, 7, "xFIRST", 1));  // replay of the older vote
            Assert.Equal("xSECOND", StoredVote(7));
        }

        [Fact]
        public void VX08_Control_CastersOwnNewerSignedVote_ReplacesItsVote()
        {
            Controller().ExchangeWinnerVote(Signed(_casterX, 7, "xFIRST", 1));
            Controller().ExchangeWinnerVote(Signed(_casterX, 7, "xSECOND", 2));
            Assert.Equal("xSECOND", StoredVote(7));
        }

        [Fact]
        public void VX08_AuditPoC_PreseedFutureHeight_Refused()
        {
            var r = Controller().ExchangeWinnerVote(Signed(_casterX, 9999, "xATTACKER_WINNER", 1));
            Assert.IsType<BadRequestObjectResult>(r.Result);
            Assert.False(Globals.CasterWinnerVoteDict.ContainsKey(9999));
        }

        [Fact]
        public void VX08_Control_NonCasterVoter_Refused()
        {
            var r = Controller().ExchangeWinnerVote(Signed(_outsider, 7, "xANY", 1));
            Assert.IsType<BadRequestObjectResult>(r.Result);
            Assert.Null(StoredVote(7));
        }

        [Fact]
        public void VX08_ResponseCarriesSignedVotes_SoRelaysAreVerifiable()
        {
            var r = Controller().ExchangeWinnerVote(Signed(_casterX, 7, "xHONEST", 1));
            var body = (string)((OkObjectResult)r.Result!).Value!;
            Assert.Contains("SignedVotes", body);
            Assert.Contains("Signature", body);
            Assert.DoesNotContain("\"Votes\"", body); // the unsigned voter→winner map is no longer served
        }

        [Fact]
        public void VX08_ForgedRelayedVote_IsNotMerged_ByTheAgreementClient()
        {
            // What a dishonest peer might return: a vote "from" caster X it fabricated.
            var fabricated = new SignedWinnerVote { BlockHeight = 7, VoterAddress = _casterX.Address, WinnerAddress = "xATTACKER", Sequence = 99, Signature = "forged" };
            var committee = new HashSet<string> { _casterX.Address };

            Assert.Equal(WinnerVoteRecordResult.Invalid, WinnerVoteStore.TryRecord(fabricated, committee.Contains));
            Assert.Null(StoredVote(7));

            var genuine = Signed(_casterX, 7, "xHONEST", 5).ToSignedVote();
            Assert.Equal(WinnerVoteRecordResult.Stored, WinnerVoteStore.TryRecord(genuine, committee.Contains));
            Assert.Equal("xHONEST", StoredVote(7));
        }
    }
}
