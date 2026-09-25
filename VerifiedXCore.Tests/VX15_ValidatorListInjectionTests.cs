using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-15 (MEDIUM): "Unauthenticated peers inject trusted entries into the validator registry".
    ///
    /// Audit PoC: POST ExchangeValidatorList naming the public caster address, with no signature, returned 200 and
    /// the entry {IP 172.28.0.20, PublicKey "04c0ffee2222…", FirstSeenAtHeight 1} appeared in the registry with
    /// IsFullyTrusted = true and the wire's first-seen height, and in the caster candidate pool. Control: a bogus
    /// caster address got 400. The client side of the exchange merged responses the same way.
    ///
    /// The route liveness-checks entries; a local stub answers that check so the injection path is really exercised.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX15_ValidatorListInjectionTests : IDisposable
    {
        private const long Tip = 500;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly int _priorValApiPort = Globals.ValAPIPort;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly LivenessStub _stub = new LivenessStub();

        private readonly (PrivateKey Key, string Pub, string Address) _casterX = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _casterY = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _outsider = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _validator = NewKey();

        public VX15_ValidatorListInjectionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx15_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();

            Globals.LastBlock = new Block { Height = Tip };
            Globals.ValidatorAddress = "";
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = _casterX.Address, PeerIP = "10.0.0.7" },
                new Peers { ValidatorAddress = _casterY.Address, PeerIP = "10.0.0.8" },
            };
            Globals.BannedIPs ??= new ConcurrentDictionary<string, Peers>();
            Globals.ValAPIPort = _stub.Port;
            Globals.NetworkValidators.Clear();
            ValidatorListExchange.ClearPending();
            Fund(_validator.Address);
        }

        public void Dispose()
        {
            _stub.Dispose();
            Globals.NetworkValidators.Clear();
            ValidatorListExchange.ClearPending();
            Globals.LastBlock = _priorLastBlock;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorValidator;
            Globals.ValAPIPort = _priorValApiPort;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>Answers any HTTP request with 200 "99.0.0" (what CheckValidatorLiveness asks for).</summary>
        private sealed class LivenessStub : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            public int Port { get; }
            public LivenessStub()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        TcpClient c;
                        try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
                        _ = Task.Run(async () =>
                        {
                            using (c)
                            {
                                try
                                {
                                    var s = c.GetStream();
                                    var buf = new byte[4096];
                                    var sb = new StringBuilder();
                                    while (!sb.ToString().Contains("\r\n\r\n"))
                                    {
                                        var n = await s.ReadAsync(buf, 0, buf.Length);
                                        if (n <= 0) break;
                                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                                    }
                                    var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 6\r\nConnection: close\r\n\r\n99.0.0");
                                    await s.WriteAsync(resp, 0, resp.Length);
                                }
                                catch { }
                            }
                        });
                    }
                });
            }
            public void Dispose() { _cts.Cancel(); try { _listener.Stop(); } catch { } }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static void Fund(string address, decimal balance = 5_001M) =>
            StateData.GetAccountStateTrei().Insert(new AccountStateTrei { Key = address, Balance = balance, Nonce = 0 });

        private static ValidatorController Controller()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("172.28.0.20");
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = ctx } };
        }

        private ValidatorListEntry Entry(string ip = "127.0.0.1", (PrivateKey Key, string Pub, string Address)? who = null) =>
            new ValidatorListEntry { Address = (who ?? _validator).Address, IPAddress = ip, PublicKey = (who ?? _validator).Pub, FirstSeenAtHeight = 1, LastSeen = 1 };

        private static ValidatorListExchangeRequest Signed((PrivateKey Key, string Pub, string Address) signer, string claimedCaster,
            List<ValidatorListEntry> entries, long? ts = null)
        {
            var req = new ValidatorListExchangeRequest { BlockHeight = Tip, CasterAddress = claimedCaster, Validators = entries, Timestamp = ts ?? TimeUtil.GetTime() };
            req.Signature = SignatureService.CreateSignature(ValidatorListExchange.SigningMessage(claimedCaster, req.Timestamp, entries), signer.Key, signer.Pub);
            return req;
        }

        private ValidatorListExchangeRequest SignedBy((PrivateKey Key, string Pub, string Address) caster, params ValidatorListEntry[] entries) =>
            Signed(caster, caster.Address, new List<ValidatorListEntry>(entries));

        private bool Registered(string address) => Globals.NetworkValidators.ContainsKey(address);

        // ── Audit PoC ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX15_AuditPoC_UnsignedListFromPublicCasterAddress_Refused_NothingInjected()
        {
            var injected = new ValidatorListEntry
            {
                Address = _validator.Address,
                IPAddress = "127.0.0.1",
                PublicKey = "04c0ffee2222",
                FirstSeenAtHeight = 1,
            };
            var req = new ValidatorListExchangeRequest { BlockHeight = Tip, CasterAddress = _casterX.Address, Validators = new List<ValidatorListEntry> { injected } };

            var r = await Controller().ExchangeValidatorList(req);

            Globals.NetworkValidators.TryGetValue(_validator.Address, out var injectedEntry);
            Assert.True(injectedEntry == null,
                $"injected: IsFullyTrusted={injectedEntry?.IsFullyTrusted} FirstSeenAtHeight={injectedEntry?.FirstSeenAtHeight} PublicKey={injectedEntry?.PublicKey}");
            Assert.IsType<BadRequestObjectResult>(r.Result);
        }

        [Fact]
        public async Task VX15_AuditControl_UnsignedFromBogusCasterAddress_Refused()
        {
            var req = new ValidatorListExchangeRequest { BlockHeight = Tip, CasterAddress = "xBOGUS_CASTER", Validators = new List<ValidatorListEntry> { Entry() } };
            var r = await Controller().ExchangeValidatorList(req);
            Assert.IsType<BadRequestObjectResult>(r.Result);
            Assert.False(Registered(_validator.Address));
        }

        [Fact]
        public async Task VX15_Control_BogusCasterAddress_Refused()
        {
            var r = await Controller().ExchangeValidatorList(SignedBy(_outsider, Entry()));
            Assert.IsType<BadRequestObjectResult>(r.Result);
            Assert.False(Registered(_validator.Address));
        }

        // ── Message authentication ─────────────────────────────────────────────────────────

        [Fact]
        public async Task VX15_OutsiderSigningAsACaster_Refused()
        {
            var r = await Controller().ExchangeValidatorList(Signed(_outsider, _casterX.Address, new List<ValidatorListEntry> { Entry() }));
            Assert.IsType<BadRequestObjectResult>(r.Result);
        }

        [Fact]
        public async Task VX15_ReplayedSignedList_Refused()
        {
            var req = SignedBy(_casterX, Entry());
            Assert.IsType<OkObjectResult>((await Controller().ExchangeValidatorList(req)).Result);
            Assert.IsType<BadRequestObjectResult>((await Controller().ExchangeValidatorList(req)).Result);
        }

        [Fact]
        public async Task VX15_StaleSignedList_Refused()
        {
            var req = Signed(_casterX, _casterX.Address, new List<ValidatorListEntry> { Entry() }, ts: TimeUtil.GetTime() - 1000);
            Assert.IsType<BadRequestObjectResult>((await Controller().ExchangeValidatorList(req)).Result);
        }

        [Fact]
        public async Task VX15_EntriesAlteredAfterSigning_Refused()
        {
            var req = SignedBy(_casterX, Entry());
            req.Validators[0].IPAddress = "203.0.113.9";
            Assert.IsType<BadRequestObjectResult>((await Controller().ExchangeValidatorList(req)).Result);
        }

        // ── Trust is local: corroboration, key binding, balance, first-seen ─────────────────

        [Fact]
        public async Task VX15_OneCastersReport_StaysPending_NotInTheRegistry()
        {
            Assert.IsType<OkObjectResult>((await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry()))).Result);
            Assert.False(Registered(_validator.Address));
            Assert.Equal(1, ValidatorListExchange.PendingReports(_validator.Address, "127.0.0.1", _validator.Pub));
        }

        [Fact]
        public async Task VX15_TwoCastersCorroborate_JoinsTrusted_WithLocalFirstSeen()
        {
            await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry()));
            await Controller().ExchangeValidatorList(SignedBy(_casterY, Entry()));

            Assert.True(Globals.NetworkValidators.TryGetValue(_validator.Address, out var nv));
            Assert.True(nv!.IsFullyTrusted);
            Assert.Equal(Tip, nv.FirstSeenAtHeight); // the wire said 1
            Assert.Equal(_validator.Pub, nv.PublicKey);
        }

        [Fact]
        public async Task VX15_SameCasterTwice_CountsOnce()
        {
            await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry()));
            await Task.Delay(1100); // a new timestamp, so a new (non-replayed) signature
            await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry()));
            Assert.False(Registered(_validator.Address));
        }

        [Fact]
        public async Task VX15_ConflictingIPsFromTwoCasters_DoNotCombine()
        {
            await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry("127.0.0.1")));
            await Controller().ExchangeValidatorList(SignedBy(_casterY, Entry("localhost")));
            Assert.False(Registered(_validator.Address));
        }

        [Fact]
        public async Task VX15_EntryWhoseKeyDoesNotDeriveTheAddress_IsDropped()
        {
            var forged = Entry();
            forged.PublicKey = _outsider.Pub; // a real key, but not the one that owns the address
            await Controller().ExchangeValidatorList(SignedBy(_casterX, forged));
            await Controller().ExchangeValidatorList(SignedBy(_casterY, forged));
            Assert.False(Registered(_validator.Address));
            Assert.Equal(0, ValidatorListExchange.PendingReports(_validator.Address, "127.0.0.1", _outsider.Pub));
        }

        [Fact]
        public async Task VX15_EntryBelowTheValidatorBalance_IsDropped()
        {
            var poor = NewKey();
            Fund(poor.Item3, 10M);
            await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry(who: poor)));
            await Controller().ExchangeValidatorList(SignedBy(_casterY, Entry(who: poor)));
            Assert.False(Registered(poor.Item3));
        }

        [Fact]
        public async Task VX15_TwoCasterCommittee_TheOtherCasterSuffices()
        {
            var self = NewKey();
            Globals.ValidatorAddress = self.Item3;
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = self.Item3, PeerIP = "10.0.0.1" },
                new Peers { ValidatorAddress = _casterX.Address, PeerIP = "10.0.0.7" },
            };
            await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry()));
            Assert.True(Registered(_validator.Address));
        }

        [Fact]
        public void VX15_RegistryWriters_StoreOnlyAKeyThatOwnsTheAddress()
        {
            // Used where a registry entry's key comes from peer data (caster lists, BootstrapStatus, proof fallback).
            Assert.Equal(_validator.Pub, NetworkValidator.BoundPublicKey(_validator.Address, _validator.Pub));
            Assert.Equal("", NetworkValidator.BoundPublicKey(_validator.Address, _outsider.Pub));
            Assert.Equal("", NetworkValidator.BoundPublicKey(_validator.Address, "04c0ffee2222"));
            Assert.Equal("", NetworkValidator.BoundPublicKey(_validator.Address, null));
        }

        // ── Response and client-side merge ─────────────────────────────────────────────────

        [Fact]
        public async Task VX15_Response_IsSignedByThisCaster()
        {
            var self = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(self);
            Globals.ValidatorAddress = self.Address;
            Globals.BlockCasters.Add(new Peers { ValidatorAddress = self.Address, PeerIP = "10.0.0.1" });

            var r = await Controller().ExchangeValidatorList(SignedBy(_casterX, Entry()));
            var body = (string)((OkObjectResult)r.Result!).Value!;
            var resp = JsonConvert.DeserializeObject<ValidatorListExchangeResponse>(body)!;

            Assert.Equal(self.Address, resp.CasterAddress);
            Assert.True(ValidatorListExchange.VerifyMessage(resp.CasterAddress, resp.Timestamp, resp.Signature, resp.Validators, consumeSignature: false, out _));
        }

        private ValidatorListExchangeResponse SignedResponse((PrivateKey Key, string Pub, string Address) signer, string claimedCaster, params ValidatorListEntry[] entries)
        {
            var list = new List<ValidatorListEntry>(entries);
            var resp = new ValidatorListExchangeResponse { BlockHeight = Tip, CasterAddress = claimedCaster, Validators = list, Timestamp = TimeUtil.GetTime() };
            resp.Signature = SignatureService.CreateSignature(ValidatorListExchange.SigningMessage(claimedCaster, resp.Timestamp, list), signer.Key, signer.Pub);
            return resp;
        }

        [Fact]
        public async Task VX15_ClientMerge_UnsignedOrWrongSenderResponses_AreIgnored()
        {
            var budget = new ValidatorListExchange.Budget(25);
            var unsigned = new ValidatorListExchangeResponse { CasterAddress = _casterX.Address, Validators = new List<ValidatorListEntry> { Entry() } };
            await ValidatorListExchange.MergeResponseAsync(unsigned, _casterX.Address, budget);
            await ValidatorListExchange.MergeResponseAsync(SignedResponse(_casterY, _casterY.Address, Entry()), _casterX.Address, budget); // asked X, Y answered
            await ValidatorListExchange.MergeResponseAsync(SignedResponse(_outsider, _casterY.Address, Entry()), _casterY.Address, budget); // forged as Y
            Assert.Equal(0, ValidatorListExchange.PendingReports(_validator.Address, "127.0.0.1", _validator.Pub));
            Assert.False(Registered(_validator.Address));
        }

        [Fact]
        public async Task VX15_ClientMerge_SameRulesAsTheRoute()
        {
            var budget = new ValidatorListExchange.Budget(25);
            await ValidatorListExchange.MergeResponseAsync(SignedResponse(_casterX, _casterX.Address, Entry()), _casterX.Address, budget);
            Assert.False(Registered(_validator.Address)); // one caster: pending
            await ValidatorListExchange.MergeResponseAsync(SignedResponse(_casterY, _casterY.Address, Entry()), _casterY.Address, budget);
            Assert.True(Globals.NetworkValidators.TryGetValue(_validator.Address, out var nv));
            Assert.Equal(Tip, nv!.FirstSeenAtHeight);
        }

        [Fact]
        public async Task VX15_ClientMerge_BudgetExhausted_DefersPromotion()
        {
            var budget = new ValidatorListExchange.Budget(0);
            await ValidatorListExchange.MergeResponseAsync(SignedResponse(_casterX, _casterX.Address, Entry()), _casterX.Address, budget);
            var outcome = await ValidatorListExchange.MergeResponseAsync(SignedResponse(_casterY, _casterY.Address, Entry()), _casterY.Address, budget);
            Assert.True(outcome.ContainsKey(ValidatorListExchange.OfferResult.Deferred));
            Assert.False(Registered(_validator.Address));
            Assert.Equal(2, ValidatorListExchange.PendingReports(_validator.Address, "127.0.0.1", _validator.Pub)); // promoted on a later round
        }

        // ── Follow-up (independent review): the other gossip ingress (AddValidatorToPool) ──

        private static NetworkValidator SignedAdvert((PrivateKey Key, string Pub, string Address) v, string ip)
        {
            var msg = $"{v.Address}:{TimeUtil.GetTime()}:{v.Pub}";
            return new NetworkValidator { Address = v.Address, PublicKey = v.Pub, IPAddress = ip, SignatureMessage = msg, Signature = SignatureService.CreateSignature(msg, v.Key, v.Pub) };
        }

        [Fact]
        public async Task VX15_Gossip_UnfundedValidator_Refused()
        {
            var poor = NewKey();
            Assert.False(await NetworkValidator.AddValidatorToPool(SignedAdvert(poor, "203.0.113.5"), "198.51.100.1"));
            Assert.False(Registered(poor.Item3));
            Assert.False(NetworkValidator.GetPendingValidators().ContainsKey(poor.Item3));
        }

        [Fact]
        public async Task VX15_Gossip_OneSource_StaysPending_TwoDistinctSourcesPromote()
        {
            Assert.True(await NetworkValidator.AddValidatorToPool(SignedAdvert(_validator, "203.0.113.5"), "198.51.100.1"));
            Assert.False(Registered(_validator.Address)); // one gossip source is not enough

            Assert.True(await NetworkValidator.AddValidatorToPool(SignedAdvert(_validator, "203.0.113.5"), "198.51.100.1"));
            Assert.False(Registered(_validator.Address)); // the same source again does not count twice

            Assert.True(await NetworkValidator.AddValidatorToPool(SignedAdvert(_validator, "203.0.113.5"), "198.51.100.2"));
            Assert.True(Registered(_validator.Address));
        }

    }
}
