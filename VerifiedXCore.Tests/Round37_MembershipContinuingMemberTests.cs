using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
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
        }

        public void Dispose()
        {
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
            var sig = await Sign(PromoteD(Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"), Member(_c, "10.0.0.3")));
            Assert.NotNull(sig);
            Assert.Equal(_a.Address, sig!.SignerAddress);
        }

        [Fact]
        public void AnHonestDemotion_PassesTheSignersRecordChecks()
        {
            // The record checks alone (the demotion's evidence rule runs before them in the async route).
            var demote = Record(_head.RecordSeq + 1, _head.EffectiveFromHeight + 100, _head.RecordHash, "Demotion", _c.Address,
                Member(_a, "10.0.0.1"), Member(_b, "10.0.0.2"));
            Assert.NotNull(CasterMembershipService.HandleSignRequest(new MembershipSignRequest { Candidate = demote, ProposerAddress = "xProposer" }));
        }
    }
}
