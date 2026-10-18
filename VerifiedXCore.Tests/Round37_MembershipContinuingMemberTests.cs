using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: a caster signs a successor membership record only when every continuing member keeps its entry
    /// (PeerIP and PublicKey) exactly as in the head. A rotation adds or removes one member; the proposer copies everyone
    /// else from the head, so an honest rotation is still signed. Driven through CasterMembershipService's sign-request
    /// handlers, the ones the validator API's membership sign route calls.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_MembershipContinuingMemberTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly string _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly Account _a, _b, _c, _d;
        private readonly CasterMembershipRecord _head;

        public Round37_MembershipContinuingMemberTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37mem_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();

            _a = Wallet(); _b = Wallet(); _c = Wallet(); _d = Wallet();
            Globals.ValidatorAddress = _a.Address;
            var a = AccountData.GetAccounts().FindOne(x => x.Address == _a.Address);
            a.IsValidating = true;
            AccountData.GetAccounts().Update(a);

            _head = Record(5, 1000, "prev", "Promotion", _c.Address, Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"));
            DbContext.DB_Config.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Insert(_head.RecordSeq, _head);
            CasterMembershipStore.InvalidateCache();
            // The successors below take effect at 1,100: a proposer at this tip builds that height.
            Globals.LastBlock = new Block { Height = 1_095 };

            // D is a validator this node knows and would promote: at 10.0.0.4, holding the caster balance, long seen.
            Globals.NetworkValidators.Clear();
            Globals.NetworkValidators[_d.Address] = new NetworkValidator { Address = _d.Address, IPAddress = "10.0.0.4", PublicKey = _d.PublicKey, FirstSeenAtHeight = 0, FirstSeenAt = 1 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _d.Address, Balance = CasterDiscoveryService.MinCasterBalance, Nonce = 0 });
        }

        public void Dispose()
        {
            Globals.NetworkValidators.TryRemove(_d.Address, out _);
            Globals.NetworkValidators.Clear();
            Globals.LastBlock = _priorLastBlock;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.ValidatorAddress = _priorValidatorAddress;
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            CasterMembershipStore.InvalidateCache();
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Account Wallet()
        {
            var acct = AccountData.CreateNewAccount(skipSave: true)!;
            acct.IsValidating = false;
            AccountData.GetAccounts().Insert(acct);
            return acct;
        }

        private static CasterInfo Member(Account a, string ip) => new() { Address = a.Address, PeerIP = ip, PublicKey = a.PublicKey };

        private static CasterMembershipRecord Record(long seq, long effective, string prev, string change, string changed, params CasterInfo[] casters)
        {
            var r = new CasterMembershipRecord
            {
                RecordSeq = seq, EffectiveFromHeight = effective, PrevRecordHash = prev, ChangeType = change, ChangedAddress = changed,
                Casters = casters.OrderBy(c => c.Address, StringComparer.Ordinal).ToList(), Signatures = new List<RecordSignature>(),
            };
            r.RecordHash = CasterMembershipStore.ComputeRecordHash(r);
            return r;
        }

        /// <summary>The successor that promotes D, with the continuing members as given.</summary>
        private CasterMembershipRecord PromoteD(params CasterInfo[] continuing) =>
            Record(_head.RecordSeq + 1, _head.EffectiveFromHeight + 100, _head.RecordHash, "Promotion", _d.Address,
                continuing.Append(Member(_d, "10.0.0.4")).ToArray());

        private static Task<RecordSignature?> Sign(CasterMembershipRecord candidate) =>
            CasterMembershipService.HandleSignRequestAsync(new MembershipSignRequest { Candidate = candidate, ProposerAddress = "xProposer" });

        /// <summary>
        /// C-012 (encoding): the signed payload joins fields with '|'. A record that folds the first member's fields into
        /// its ChangedAddress and lists one member fewer has the same bytes, so the signatures on the honest record were
        /// valid on it too, and it could install a committee nobody signed. A field holding '|' is refused at validation
        /// (every append and catch-up), and an honest record is unchanged.
        /// </summary>
        [Fact]
        public void ARecordWithTheSameBytesButOtherFields_IsRefused()
        {
            var honest = PromoteD(Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"));
            var payload = CasterMembershipStore.CanonicalPayload(honest);
            foreach (var signer in new[] { _a, _b })
                honest.Signatures.Add(new RecordSignature { SignerAddress = signer.Address, Signature = SignatureService.CreateSignature(payload, signer.GetPrivKey, signer.PublicKey) });
            Assert.True(CasterMembershipStore.ValidateSuccessor(_head, honest, out var why), why);

            var first = honest.Casters[0];
            var forged = new CasterMembershipRecord
            {
                RecordSeq = honest.RecordSeq, EffectiveFromHeight = honest.EffectiveFromHeight, PrevRecordHash = honest.PrevRecordHash,
                ChangeType = honest.ChangeType, ChangedAddress = $"{honest.ChangedAddress}|{first.Address}|{first.PeerIP}|{first.PublicKey}",
                Casters = honest.Casters.Skip(1).ToList(), Signatures = honest.Signatures,
            };
            forged.RecordHash = CasterMembershipStore.ComputeRecordHash(forged);
            Assert.Equal(payload, CasterMembershipStore.CanonicalPayload(forged));                  // the same bytes, other fields
            Assert.False(CasterMembershipStore.ValidateSuccessor(_head, forged, out why));
            Assert.Contains("payload separator", why);
        }

        [Fact]
        public async Task ARotationThatChangesAnotherMembersPeerIp_IsNotSigned()
        {
            Assert.Null(await Sign(PromoteD(Member(_a, "10.0.0.1"), Member(_b, "203.0.113.9"), Member(_c, "10.0.0.3"))));
        }

        [Fact]
        public async Task ARotationThatChangesAnotherMembersPublicKey_IsNotSigned()
        {
            var b = Member(_b, "10.0.0.2");
            b.PublicKey = _d.PublicKey;
            Assert.Null(await Sign(PromoteD(Member(_a, "10.0.0.1"), b, Member(_c, "10.0.0.3"))));
        }

        [Fact]
        public async Task AnHonestPromotion_IsSigned()
        {
            SeeD("10.0.0.4");
            var sig = await Sign(PromoteD(Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3")));
            Assert.NotNull(sig);
            Assert.Equal(_a.Address, sig!.SignerAddress);
        }

        /// <summary>This node's view of D, the validator being promoted (NetworkValidators, where the proposer reads it too).</summary>
        private void SeeD(string ip, string? publicKey = null) =>
            Globals.NetworkValidators[_d.Address] = new NetworkValidator { Address = _d.Address, IPAddress = "::ffff:" + ip, PublicKey = publicKey ?? _d.PublicKey };

        /// <summary>
        /// C-012: the promoted member's PeerIP is where it is probed, and it is demoted on that address's silence. A record
        /// naming another address for it - proposed so, or as a lower-hash sibling of the honest one - was signed, as was
        /// one naming another key. A signer now signs a promotion only for the entry it sees for that validator itself.
        /// </summary>
        [Fact]
        public async Task APromotionNamingAnotherAddressOrKeyForTheNewMember_IsNotSigned()
        {
            var continuing = new[] { Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3") };
            try
            {
                Globals.NetworkValidators.TryRemove(_d.Address, out _);
                Assert.Null(await Sign(PromoteD(continuing)));                       // a validator this node does not know

                SeeD("198.51.100.7");
                Assert.Null(await Sign(PromoteD(continuing)));                       // the record says 10.0.0.4

                SeeD("10.0.0.4", publicKey: _c.PublicKey);
                Assert.Null(await Sign(PromoteD(continuing)));                       // another key

                SeeD("10.0.0.4");
                Assert.NotNull(await Sign(PromoteD(continuing)));                    // as this node sees it
            }
            finally { Globals.NetworkValidators.TryRemove(_d.Address, out _); }
        }

        [Fact]
        public async Task APromotionOfAnAddressThisNodeDoesNotKnowAsAValidator_IsNotSigned()
        {
            Globals.NetworkValidators.Clear();
            Assert.Null(await Sign(PromoteD(Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"))));
        }

        [Fact]
        public async Task APromotionOfAValidatorWithoutTheCasterBalance_IsNotSigned()
        {
            var state = StateData.GetAccountStateTrei().FindOne(x => x.Key == _d.Address);
            state.Balance = CasterDiscoveryService.MinCasterBalance - 1;
            StateData.GetAccountStateTrei().UpdateSafe(state);
            Assert.Null(await Sign(PromoteD(Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"))));
        }

        [Fact]
        public async Task APromotionOfAValidatorFirstSeenJustNow_IsNotSigned()
        {
            Globals.NetworkValidators[_d.Address].FirstSeenAtHeight = Globals.LastBlock?.Height > 0 ? Globals.LastBlock.Height : 1;
            Globals.NetworkValidators[_d.Address].FirstSeenAt = VerifiedXCore.Utilities.TimeUtil.GetTime();
            Assert.Null(await Sign(PromoteD(Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"))));
        }

        [Fact]
        public async Task APromotionSeatingAValidatorAtAnotherIpOrUnderAnotherKey_IsNotSigned()
        {
            var continuing = new[] { Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3") };
            var otherIp = Record(_head.RecordSeq + 1, _head.EffectiveFromHeight + 100, _head.RecordHash, "Promotion", _d.Address,
                continuing.Append(Member(_d, "203.0.113.9")).ToArray());
            Assert.Null(await Sign(otherIp));

            var otherKey = Member(_d, "10.0.0.4");
            otherKey.PublicKey = _b.PublicKey;
            var withOtherKey = Record(_head.RecordSeq + 1, _head.EffectiveFromHeight + 100, _head.RecordHash, "Promotion", _d.Address,
                continuing.Append(otherKey).ToArray());
            Assert.Null(await Sign(withOtherKey));
        }

        [Fact]
        public async Task ARotationTakingEffectFarBeyondOurTip_IsNotSigned()
        {
            // Signed, it would block every later rotation: each successor must take effect later still.
            var farOff = Record(_head.RecordSeq + 1, 999_999_999, _head.RecordHash, "Promotion", _d.Address,
                Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"), Member(_d, "10.0.0.4"));
            Assert.Null(await Sign(farOff));

            // What a proposer whose tip leads ours by the allowed margin builds is still signed.
            var leading = Record(_head.RecordSeq + 1, CasterMembershipStore.ComputeRotationEffectiveHeight(_head.EffectiveFromHeight, Globals.LastBlock.Height + CasterMembershipStore.MaxProposerTipLead),
                _head.RecordHash, "Promotion", _d.Address, Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"), Member(_d, "10.0.0.4"));
            Assert.NotNull(await Sign(leading));
        }

        [Fact]
        public void AnHonestDemotion_PassesTheSignersRecordChecks()
        {
            // The record checks alone (the demotion's evidence rule runs before them in the async route).
            var demote = Record(_head.RecordSeq + 1, _head.EffectiveFromHeight + 100, _head.RecordHash, "Demotion", _c.Address,
                Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"));
            Assert.NotNull(CasterMembershipService.HandleSignRequest(new MembershipSignRequest { Candidate = demote, ProposerAddress = "xProposer" }));
        }
        /// <summary>
        /// A demotion this node has no evidence for is refused even when the member's listed address answers as an old
        /// binary would (404 on the version route): nothing proves the member gave that answer. The node's own evidence
        /// (here, an outdated version it saw itself) still lets it sign.
        /// </summary>
        [Fact]
        public async Task ADemotion_IsNotSignedOnAnAnswerAtTheMembersAddress_OnlyOnThisNodesOwnEvidence()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var stop = new System.Threading.CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token);
                        var stream = client.GetStream();
                        await stream.ReadAsync(new byte[4096]);
                        var reply = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(reply);
                    }
                    catch { return; }
                }
            });
            var priorPort = Globals.ValAPIPort;
            var priorFactory = Globals.HttpClientFactory;
            Globals.ValAPIPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            Globals.HttpClientFactory = new PlainHttpClientFactory();
            try
            {
                // The head lists C at the address that answers 404.
                var e = Wallet();
                var head = Record(_head.RecordSeq + 1, _head.EffectiveFromHeight + 50, _head.RecordHash, "Promotion", e.Address,
                    Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"), Member(e, "127.0.0.1"));
                DbContext.DB_Config.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Insert(head.RecordSeq, head);
                CasterMembershipStore.InvalidateCache();

                var demote = Record(head.RecordSeq + 1, head.EffectiveFromHeight + 10, head.RecordHash, "Demotion", e.Address,
                    Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"));
                Assert.Null(await Sign(demote));

                CasterSeatService.NoteOutdated(e.Address);
                Assert.NotNull(await Sign(demote));
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
                Globals.ValAPIPort = priorPort;
                Globals.HttpClientFactory = priorFactory;
            }
        }

        private sealed class PlainHttpClientFactory : System.Net.Http.IHttpClientFactory
        {
            public System.Net.Http.HttpClient CreateClient(string name) => new();
        }
    }
}
