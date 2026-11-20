using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The address another node gives for a caster or validator is an IPv4 address in dotted form and nothing else. This
    /// node writes it between "http://" and the port of the requests it sends that peer, so text that carries a port, a
    /// path or a host name is refused where it arrives (a promotion, the live caster list, a peer's caster list, the
    /// validator pool, a validator list entry, a promotion proposal, a membership record asked to be signed) and is not
    /// dialed where this node holds it (the live caster list, a member's entry in a stored membership record).
    /// Driven through the entry points the validator API's routes and the block loop call; a local listener counts the
    /// requests this node sends.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PeerAddressTests : IDisposable
    {
        private const long Height = 1_095;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly string _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly List<CasterInfo> _priorKnownCasters = Globals.KnownCasters.ToList();
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;
        private readonly int _priorValApiPort = Globals.ValAPIPort;
        private readonly bool _priorSweepComplete = Globals.ValidatorLivenessSweepComplete;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly Listener _listener = new();
        private readonly Account _me, _b, _c, _d;

        public PeerAddressTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"peeraddr_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();

            _me = Wallet(); _b = Wallet(); _c = Wallet(); _d = Wallet();
            Globals.ValidatorAddress = _me.Address;
            var me = AccountData.GetAccounts().FindOne(x => x.Address == _me.Address);
            me.IsValidating = true;
            AccountData.GetAccounts().Update(me);

            Globals.LastBlock = new Block { Height = Height };
            Globals.IsBlockCaster = false;
            Globals.ValidatorLivenessSweepComplete = false;
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = _b.Address, PeerIP = "10.0.0.2", ValidatorPublicKey = _b.PublicKey, IsValidator = true },
            };
            // A port nothing listens on, so a request to a peer's real address ends at once; a test that expects one
            // points it at the listener.
            Globals.ValAPIPort = ClosedPort();
            Globals.HttpClientFactory = new SocketClients();
            Globals.NetworkValidators.Clear();
            Globals.NetworkValidators[_c.Address] = Known(_c, "10.0.0.3");
            Globals.NetworkValidators[_d.Address] = Known(_d, "10.0.0.4");
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _d.Address, Balance = CasterDiscoveryService.MinCasterBalance, Nonce = 0 });
            CasterSeatService.ResetForTests();
            AttestationGuard.ResetForTests();
        }

        public void Dispose()
        {
            _listener.Dispose();
            CasterSeatService.ResetForTests();
            AttestationGuard.ResetForTests();
            Globals.NetworkValidators.Clear();
            Globals.ValidatorLivenessSweepComplete = _priorSweepComplete;
            Globals.ValAPIPort = _priorValApiPort;
            Globals.HttpClientFactory = _priorFactory;
            Globals.IsBlockCaster = _priorIsBlockCaster;
            Globals.BlockCasters = _priorCasters;
            lock (Globals.KnownCastersLock)
            {
                Globals.KnownCasters.Clear();
                Globals.KnownCasters.AddRange(_priorKnownCasters);
            }
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

        /// <summary>Answers every HTTP request with 200 and <see cref="Body"/>, and keeps each request line.</summary>
        private sealed class Listener : IDisposable
        {
            private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
            public ConcurrentQueue<string> Requests { get; } = new();
            public int Port { get; }
            public string Body { get; set; } = "99.0.0";

            public Listener()
            {
                _tcp.Start();
                Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        TcpClient client;
                        try { client = await _tcp.AcceptTcpClientAsync(); } catch { return; }
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                try
                                {
                                    var stream = client.GetStream();
                                    var buffer = new byte[4096];
                                    var head = new StringBuilder();
                                    while (!head.ToString().Contains("\r\n\r\n"))
                                    {
                                        var n = await stream.ReadAsync(buffer, 0, buffer.Length);
                                        if (n <= 0) break;
                                        head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                                    }
                                    Requests.Enqueue(head.ToString().Split("\r\n")[0]);
                                    var answer = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n\r\n{Body}");
                                    await stream.WriteAsync(answer, 0, answer.Length);
                                }
                                catch { }
                            }
                        });
                    }
                });
            }

            /// <summary>Waits up to <paramref name="milliseconds"/> for a request to arrive.</summary>
            public async Task<bool> SawARequest(int milliseconds = 5_000)
            {
                for (var waited = 0; waited < milliseconds && Requests.IsEmpty; waited += 25) await Task.Delay(25);
                return !Requests.IsEmpty;
            }

            public void Dispose() { try { _tcp.Stop(); } catch { } }
        }

        /// <summary>The node's own kind of client: a plain one over real sockets.</summary>
        private sealed class SocketClients : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new();
        }

        private static int ClosedPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static Account Wallet()
        {
            var acct = AccountData.CreateNewAccount(skipSave: true)!;
            acct.IsValidating = false;
            AccountData.GetAccounts().Insert(acct);
            return acct;
        }

        private static NetworkValidator Known(Account a, string ip) =>
            new() { Address = a.Address, IPAddress = ip, PublicKey = a.PublicKey, FirstSeenAtHeight = 0, FirstSeenAt = 1 };

        private static CasterInfo Member(Account a, string ip) => new() { Address = a.Address, PeerIP = ip, PublicKey = a.PublicKey };

        /// <summary>Text that is not an IP address. Nothing is sent because of it; the listener would show a request that was.</summary>
        private string NotAnAddress => $"127.0.0.1:{_listener.Port}/elsewhere#";

        // ── The rule ─────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("203.0.113.9", "203.0.113.9")]
        [InlineData("10.0.0.1", "10.0.0.1")]
        [InlineData("127.0.0.1", "127.0.0.1")]
        [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
        [InlineData("3405803785", "203.0.113.9")]
        [InlineData("0xcb.0.113.9", "203.0.113.9")]
        public void AnIPv4Address_IsAPeerAddress_ReadInDottedForm(string address, string dotted)
        {
            Assert.True(InputValidationHelper.IsPeerIPAddress(address));
            Assert.True(InputValidationHelper.TryGetPeerIP(address, out var ip));
            Assert.Equal(dotted, ip);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" 203.0.113.9")]
        [InlineData("203.0.113.9 ")]
        [InlineData("203.0.113.9.")]
        [InlineData("203.0.113.9\n")]
        [InlineData("203.0.113.9:8080")]
        [InlineData("203.0.113.9/path")]
        [InlineData("203.0.113.9:8080/path#")]
        [InlineData("203.0.113.9?x=")]
        [InlineData("203.0.113.9#")]
        [InlineData("user@203.0.113.9")]
        [InlineData("peer.example")]
        [InlineData("localhost")]
        [InlineData("203.0.113.x")]
        [InlineData("203.0.::ffff:113.9")]
        [InlineData("::ffff:::ffff:203.0.113.9")]
        [InlineData("::1")]
        [InlineData("[::1]")]
        [InlineData("[::1]:8080")]
        [InlineData("2001:db8::1")]
        public void AnythingElse_IsNotAPeerAddress(string? address)
        {
            Assert.False(InputValidationHelper.IsPeerIPAddress(address));
            Assert.False(InputValidationHelper.TryGetPeerIP(address, out var ip));
            Assert.Equal("", ip);
        }

        // ── A promotion ──────────────────────────────────────────────────────────────────────────

        /// <summary>A promotion of this node, signed by caster B over the list it carries.</summary>
        private CasterPromotionRequest PromotionFromB(params CasterInfo[] list)
        {
            var json = CasterDiscoveryService.GetCanonicalCasterListJson(list);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            var message = $"PROMOTE|{_me.Address}|{Height}|{_b.Address}|{hash}";
            return new CasterPromotionRequest
            {
                PromotedAddress = _me.Address,
                BlockHeight = Height,
                CasterList = list.ToList(),
                PromoterAddress = _b.Address,
                PromoterSignature = SignatureService.CreateSignature(message, _b.GetPrivKey, _b.PublicKey),
            };
        }

        [Theory]
        [InlineData("10.0.0.3:8080")]
        [InlineData("10.0.0.3/path")]
        [InlineData("10.0.0.3:8080/path#")]
        [InlineData("10.0.0.3?x=")]
        [InlineData("peer.example")]
        public async Task APromotionWhoseListHoldsAnAddressThatIsNotAnIPAddress_IsRefused_AndTheLiveListIsKept(string address)
        {
            var before = Globals.BlockCasters;
            var promotion = PromotionFromB(Member(_b, "10.0.0.2"), Member(_me, "10.0.0.1"), Member(_c, address));

            var answer = await CasterDiscoveryService.HandlePromotion(promotion);

            Assert.Contains("not an IP address", answer);
            Assert.Same(before, Globals.BlockCasters);
            Assert.DoesNotContain(Globals.BlockCasters, p => p.ValidatorAddress == _c.Address);
            Assert.False(Globals.IsBlockCaster);
        }

        [Fact]
        public async Task APromotionWhoseListHoldsIPAddresses_IsAccepted_AndTheListIsStoredWithoutThePrefix()
        {
            // The promoted node pulls the membership record from the list's addresses afterwards: let that reach the listener.
            Globals.ValAPIPort = _listener.Port;
            var promotion = PromotionFromB(Member(_b, "127.0.0.1"), Member(_me, "::ffff:127.0.0.1"), Member(_c, ""));

            var answer = await CasterDiscoveryService.HandlePromotion(promotion);

            Assert.Equal("accepted", answer);
            Assert.True(Globals.IsBlockCaster);
            var stored = Globals.BlockCasters.ToDictionary(p => p.ValidatorAddress!, p => p.PeerIP);
            Assert.Equal(3, stored.Count);
            Assert.Equal("127.0.0.1", stored[_b.Address]);
            Assert.Equal("127.0.0.1", stored[_me.Address]);
            Assert.Equal("", stored[_c.Address]);
            Assert.True(await _listener.SawARequest(), "the record pull did not reach the promoter's address");
            await Task.Delay(250); // the pull ends on the listener's answer, before the store is closed
        }

        // ── The live caster list ─────────────────────────────────────────────────────────────────

        [Fact]
        public void AnEntryForTheLiveList_IsAddedWithAnIPAddressOrNone_AndRefusedWithAnythingElse()
        {
            Globals.BlockCasters = new ConcurrentBag<Peers>();

            Assert.False(CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(new Peers { ValidatorAddress = _c.Address, PeerIP = NotAnAddress }));
            Assert.False(CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(new Peers { ValidatorAddress = _c.Address, PeerIP = "10.0.::ffff:0.3" }));
            Assert.Empty(Globals.BlockCasters);

            Assert.True(CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(new Peers { ValidatorAddress = _c.Address, PeerIP = "::ffff:10.0.0.3" }));
            Assert.True(CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(new Peers { ValidatorAddress = _d.Address, PeerIP = "" }));
            Assert.False(CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(new Peers { ValidatorAddress = _b.Address, PeerIP = "167772163" })); // 10.0.0.3 again
            Assert.Equal("10.0.0.3", Globals.BlockCasters.Single(p => p.ValidatorAddress == _c.Address).PeerIP);
            Assert.Equal("", Globals.BlockCasters.Single(p => p.ValidatorAddress == _d.Address).PeerIP);
        }

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

        private CasterMembershipRecord StoreHead(params CasterInfo[] casters)
        {
            var head = Record(5, 1_000, "prev", "Promotion", _c.Address, casters);
            DbContext.DB_Config.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Insert(head.RecordSeq, head);
            CasterMembershipStore.InvalidateCache();
            return head;
        }

        /// <summary>
        /// The membership record in this node's store is where the live list is filled from at startup and on every
        /// record. A member whose stored entry holds text that is not an IP address stays a member of the record, and is
        /// left out of the list this node dials; the others are taken as before.
        /// </summary>
        [Fact]
        public void AStoredRecordMemberWhoseAddressIsNotAnIPAddress_IsLeftOutOfTheLiveList()
        {
            StoreHead(Member(_me, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, NotAnAddress));
            Globals.BlockCasters = new ConcurrentBag<Peers>();
            Globals.NetworkValidators.Clear(); // an entry added to the live list is also given a pool entry at its address

            CasterMembershipService.ReconcileBlockCastersToRecord(CasterMembershipStore.GetCurrent()!);

            var live = Globals.BlockCasters.ToDictionary(p => p.ValidatorAddress!, p => p.PeerIP);
            Assert.Equal(2, live.Count);
            Assert.Equal("10.0.0.1", live[_me.Address]);
            Assert.Equal("10.0.0.2", live[_b.Address]);
            Assert.Contains(CasterMembershipStore.GetCurrent()!.Casters, c => c.Address == _c.Address);
            Assert.Equal(new[] { _b.Address, _me.Address }.OrderBy(a => a), Globals.NetworkValidators.Keys.OrderBy(a => a));
        }

        // ── The attestation after a block ────────────────────────────────────────────────────────

        private Block AcceptedBlock(long height) =>
            new() { Height = height, Hash = $"hash{height}", PrevHash = $"hash{height - 1}", Validator = _b.Address, Version = ConsensusCertificateRules.MinBlockVersion };

        private void CastWith(string peerAddress)
        {
            Globals.IsBlockCaster = true;
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = _me.Address, PeerIP = "", ValidatorPublicKey = _me.PublicKey, IsValidator = true },
                new Peers { ValidatorAddress = _b.Address, PeerIP = peerAddress, ValidatorPublicKey = _b.PublicKey, IsValidator = true },
            };
        }

        /// <summary>An entry of the live list whose address is not an IP address is not sent the attestation.</summary>
        [Fact]
        public async Task TheAttestation_IsNotSentToAnEntryWhoseAddressIsNotAnIPAddress()
        {
            CastWith(NotAnAddress);

            await ConsensusAttestationPublisher.PublishLocalAsync(AcceptedBlock(100));

            await Task.Delay(200);
            Assert.Empty(_listener.Requests);
        }

        [Fact]
        public async Task TheAttestation_IsSentToACasterAtItsIPAddress()
        {
            Globals.ValAPIPort = _listener.Port;
            CastWith("::ffff:127.0.0.1");

            await ConsensusAttestationPublisher.PublishLocalAsync(AcceptedBlock(101));

            Assert.True(await _listener.SawARequest());
            Assert.Equal("POST /valapi/validator/SubmitAttestation HTTP/1.1", Assert.Single(_listener.Requests));
        }

        // ── The validator pool ───────────────────────────────────────────────────────────────────

        private static NetworkValidator Advertisement(Account validator, string ip)
        {
            var message = $"{validator.Address}:{TimeUtil.GetTime()}:{validator.PublicKey}";
            return new NetworkValidator
            {
                Address = validator.Address, IPAddress = ip, PublicKey = validator.PublicKey, UniqueName = "v",
                SignatureMessage = message, Signature = SignatureService.CreateSignature(message, validator.GetPrivKey, validator.PublicKey),
            };
        }

        /// <summary>A validator's advertisement relayed by a peer carries the validator's IP as the peer gives it.</summary>
        [Fact]
        public async Task ARelayedAdvertisement_EntersThePoolWithAnIPAddress_AndIsRefusedWithAnythingElse()
        {
            var funded = Wallet();
            var other = Wallet();
            foreach (var v in new[] { funded, other })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = ValidatorService.ValidatorRequiredAmount(), Nonce = 0 });
            const string relay = "10.77.0.1";

            Assert.False(await NetworkValidator.AddValidatorToPool(Advertisement(funded, NotAnAddress), relay));
            Assert.False(Globals.NetworkValidators.ContainsKey(funded.Address));
            Assert.False(NetworkValidator.GetPendingValidators().ContainsKey(funded.Address));
            Assert.Empty(_listener.Requests);

            Assert.True(await NetworkValidator.AddValidatorToPool(Advertisement(other, "::ffff:10.0.0.9"), relay));
            Assert.Equal("10.0.0.9", NetworkValidator.GetPendingValidators()[other.Address].IPAddress);
        }

        [Fact]
        public void AValidatorListEntry_IsAcceptableWithAnIPAddress_AndRefusedWithAnythingElse()
        {
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _c.Address, Balance = ValidatorService.ValidatorRequiredAmount(), Nonce = 0 });

            Assert.False(ValidatorListExchange.IsAcceptableEntry(new ValidatorListEntry { Address = _c.Address, IPAddress = NotAnAddress, PublicKey = _c.PublicKey }, out var why));
            Assert.Contains("not an IP address", why);
            Assert.False(ValidatorListExchange.IsAcceptableEntry(new ValidatorListEntry { Address = _c.Address, IPAddress = "peer.example", PublicKey = _c.PublicKey }, out _));
            Assert.True(ValidatorListExchange.IsAcceptableEntry(new ValidatorListEntry { Address = _c.Address, IPAddress = "10.0.0.3", PublicKey = _c.PublicKey }, out why), why);
        }

        // ── The checks this node runs on a candidate's address ───────────────────────────────────

        [Fact]
        public async Task ACandidateCheck_DialsAnIPAddress_AndNothingElse()
        {
            Assert.Equal(CasterDiscoveryService.VersionCheckResult.Unreachable, (await CasterDiscoveryService.CheckCandidateVersionDetailed(NotAnAddress, _d.Address)).Status);
            Assert.False(await CasterDiscoveryService.CheckCandidateHealth(NotAnAddress, _d.Address));
            Assert.False(await NetworkValidator.CheckValidatorLiveness(NotAnAddress));
            await Task.Delay(200);
            Assert.Empty(_listener.Requests);

            Globals.ValAPIPort = _listener.Port;
            await NetworkValidator.CheckValidatorLiveness("127.0.0.1");
            Assert.Equal("GET /valapi/validator/GetWalletVersion HTTP/1.1", Assert.Single(_listener.Requests));
        }

        /// <summary>A promotion proposal names the candidate's address; a caster asked to agree checks the candidate there.</summary>
        [Fact]
        public async Task APromotionProposalNamingAnAddressThatIsNotAnIPAddress_IsNotAgreedTo_AndNothingIsDialed()
        {
            Globals.IsBlockCaster = true;

            var answer = await CasterDiscoveryService.EvaluatePromotionProposal(new PromotionProposalRequest
            {
                CandidateAddress = _d.Address, CandidateIP = NotAnAddress, BlockHeight = Height, ProposerAddress = _b.Address,
            });

            Assert.False(answer.Accepted);
            Assert.Contains("not an IP address", answer.Reason);
            await Task.Delay(200);
            Assert.Empty(_listener.Requests);
        }

        // ── A peer's live caster list ────────────────────────────────────────────────────────────

        private static string CasterListAnswer(params CasterInfo[] casters) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(new SignedCasterListResponse { AsOfBlockHeight = (int)Height, Casters = casters.ToList() });

        /// <summary>
        /// Before a membership record exists, a caster no peer lists stands down and takes a peer's caster list. It still
        /// stands down on a list that holds text that is not an IP address, and keeps the list it had.
        /// </summary>
        [Fact]
        public async Task APeersCasterList_IsTakenWithIPAddresses_AndNotTakenWithAnythingElse()
        {
            Globals.ValAPIPort = _listener.Port;
            void CastBesideB()
            {
                Globals.IsBlockCaster = true;
                Globals.BlockCasters = new ConcurrentBag<Peers>
                {
                    new Peers { ValidatorAddress = _b.Address, PeerIP = "127.0.0.1", ValidatorPublicKey = _b.PublicKey, IsValidator = true },
                };
            }

            CastBesideB();
            _listener.Body = CasterListAnswer(Member(_b, "127.0.0.1"), Member(_c, NotAnAddress));
            Assert.True(await CasterDiscoveryService.PerformEvictionAwarenessCheckAsync());
            Assert.False(Globals.IsBlockCaster);
            Assert.Equal(_b.Address, Assert.Single(Globals.BlockCasters).ValidatorAddress);

            CastBesideB();
            _listener.Body = CasterListAnswer(Member(_b, "127.0.0.1"), Member(_c, "::ffff:10.0.0.3"));
            Assert.True(await CasterDiscoveryService.PerformEvictionAwarenessCheckAsync());
            Assert.Equal("10.0.0.3", Globals.BlockCasters.Single(p => p.ValidatorAddress == _c.Address).PeerIP);
        }

        // ── A member's entry in a stored membership record ───────────────────────────────────────

        /// <summary>
        /// A record is judged by its signatures, so a member's entry is read as the record gives it. Where this node
        /// dials a member from the record, an entry that is not an IP address is skipped, like a member with no address.
        /// </summary>
        [Fact]
        public async Task ARecordMemberWhoseAddressIsNotAnIPAddress_IsNotDialed()
        {
            var record = StoreHead(Member(_me, "10.0.0.1"), Member(_b, NotAnAddress), Member(_c, NotAnAddress));

            await CasterMembershipService.BroadcastRecordAsync(record);
            Assert.False(await CasterSeatService.IsCastingAsync(NotAnAddress));
            Assert.False(await CasterSeatService.IsLegacyBuildAsync(NotAnAddress));
            Assert.Null(await VreeService.HttpGet(NotAnAddress, "Health"));
            Assert.Null(await VreeService.HttpPost(NotAnAddress, "ProbeTip", "{}"));
            // The stall survey still counts the member as one of the committee, and as one that did not answer.
            var unanswering = NetworkStallSurvey.AssembleKnownSet(record).Where(n => n.Ip == NotAnAddress).ToList();
            Assert.True(Assert.Single(unanswering).IsCommittee);
            Assert.Equal(-1, Assert.Single(await NetworkStallSurvey.CollectAsync(unanswering)).Height);
            var view = new VerifiedXCore.Bitcoin.Services.BaseBridgeService.SignerView(Array.Empty<string>(), 4, 1, 0,
                new[] { VerifiedXCore.Bitcoin.Services.BaseSignerSetPolicy.BaseAddressOf(_b.PublicKey), VerifiedXCore.Bitcoin.Services.BaseSignerSetPolicy.BaseAddressOf(_me.PublicKey) }, 1);
            Assert.Equal(_me.Address, Assert.Single(VerifiedXCore.Bitcoin.Services.UserBridgeMintService.ContractSigners(view)).VfxAddress);
            await Task.Delay(200);
            Assert.Empty(_listener.Requests);

            // A member at an IP address is dialed there.
            Globals.ValAPIPort = _listener.Port;
            await CasterMembershipService.BroadcastRecordAsync(Record(6, 1_100, record.RecordHash, "Demotion", _c.Address, Member(_me, ""), Member(_b, "127.0.0.1")));
            Assert.Equal("POST /valapi/validator/AnnounceMembershipRecord HTTP/1.1", Assert.Single(_listener.Requests));
        }

        // ── A membership record asked to be signed ───────────────────────────────────────────────

        private static RecordSignature SignatureOf(Account signer, CasterMembershipRecord candidate) => new()
        {
            SignerAddress = signer.Address,
            Signature = SignatureService.CreateSignature(CasterMembershipStore.CanonicalPayload(candidate), signer.GetPrivKey, signer.PublicKey),
        };

        /// <summary>The successor of <paramref name="head"/> that promotes D at <paramref name="address"/>, proposed and signed by member B.</summary>
        private CasterMembershipRecord PromoteD(CasterMembershipRecord head, string address)
        {
            var candidate = Record(head.RecordSeq + 1, head.EffectiveFromHeight + 100, head.RecordHash, "Promotion", _d.Address,
                head.Casters.Append(Member(_d, address)).ToArray());
            candidate.Signatures.Add(SignatureOf(_b, candidate));
            return candidate;
        }

        private static Task<RecordSignature?> AskToSign(CasterMembershipRecord candidate) =>
            CasterMembershipService.HandleSignRequestAsync(new MembershipSignRequest { Candidate = candidate, ProposerAddress = "xProposer" });

        /// <summary>
        /// A caster signs a promotion whose new entry is the validator where this node sees it. When what this node holds
        /// for that validator is not an IP address, the record that would carry it to every node is not signed.
        /// </summary>
        [Fact]
        public async Task APromotionRecordWhoseNewEntryIsNotAnIPAddress_IsNotSigned()
        {
            var head = StoreHead(Member(_me, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3"));

            Globals.NetworkValidators[_d.Address] = Known(_d, NotAnAddress);
            Assert.Null(await AskToSign(PromoteD(head, NotAnAddress)));

            Globals.NetworkValidators[_d.Address] = Known(_d, "10.0.0.4");
            Assert.NotNull(await AskToSign(PromoteD(head, "10.0.0.4")));
        }
    }
}
