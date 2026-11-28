using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxBlock = VerifiedXCore.Models.Block;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Room for signing sessions on a validator. A place is charged to a party that is not made for nothing: a session
    /// led by a holder of the key or by a caster to its leader, in a room that follows the validator set; a session led by
    /// a requester to its vault as well, and to no other vault. A leader has what signing needs (it signs one input at a
    /// time), one session for an input, and a session whose Round 2 does not come gives its place up after two minutes.
    /// Every test drives <c>/frost/sign/start</c> (and Round 2) of a validator that holds a real share of the vaults'
    /// keys.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SigningRoomTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        /// <summary>This validator: a participant of the key ceremony the shares used here come from.</summary>
        private const string Me = "xAlpha";

        private static readonly Lazy<(Dictionary<string, string> KeyPackages, string PubkeyPackage)> Dkg = new(FrostAggregateParticipantOrderTests.RealDkg);

        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly string? _priorValidator = Globals.ValidatorAddress;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly Func<string, Task<int?>> _priorConfirmations = FrostSigningAuthorization.ParentConfirmations;
        private readonly Dictionary<string, FrostSigningStartRequest> _announced = new();
        private TestServer? _server;
        private HttpClient? _client;

        public SigningRoomTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            Globals.ValidatorAddress = Me;
            FrostSessionStorage.SigningSessions.Clear();
            FrostWithdrawalSigningTracker.ResetForTests();
            VBTCValidatorRegistry.InvalidateCache();
        }

        public override void Dispose()
        {
            _client?.Dispose();
            _server?.Dispose();
            FrostSessionStorage.SigningSessions.Clear();
            FrostWithdrawalSigningTracker.ResetForTests();
            FrostSigningAuthorization.ParentConfirmations = _priorConfirmations;
            Globals.IsChainSynced = _priorSynced;
            Globals.ValidatorAddress = _priorValidator;
            Globals.BTCNetwork = _priorNetwork;
            VBTCValidatorRegistry.InvalidateCache();
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;
        private static long Now => TimeUtil.GetTime();

        // ── The chain ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Buries a Bitcoin block and has the validators report it.</summary>
        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            await Apply(Tip + 1, Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, 0))));
        }

        /// <summary>The vault is paid <paramref name="coins"/> coins of <paramref name="sats"/> each, in one Bitcoin transaction.</summary>
        private Task Deposit(NBitcoin.Script vault, int coins, long sats) =>
            Report(_btc.Mine(Pay(null, Enumerable.Repeat((vault, sats), coins).ToArray())));

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        /// <summary>Judged as a block's transaction, then mined in the next block.</summary>
        private static async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private static VfxTx RequestTx(string vault, decimal amount, V from) =>
            SignedBy(from, from.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") });

        /// <summary>A withdrawal from a watched vault, mined: the transaction the chain fixed for it.</summary>
        private async Task<(VfxTx Request, BtcTx Pinned)> Withdraw(string vault, decimal amount, V? from = null)
        {
            var request = await Mine(RequestTx(vault, amount, from ?? Minter));
            var row = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault))!;
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            return (request, BtcTx.Parse(Assert.Single(row.Pins).TxHex, Network.Main));
        }

        private V Funded()
        {
            var key = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = key.Address, Balance = 100M, Nonce = 0 });
            return key;
        }

        /// <summary>This validator holds a share of the key (a real one, from a ceremony of its own, filed under the key's UID).</summary>
        private static void HoldShare(string keyUid) =>
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore
            {
                SmartContractUID = keyUid, ValidatorAddress = Me, KeyPackage = Dkg.Value.KeyPackages[Me], PubkeyPackage = Dkg.Value.PubkeyPackage,
                GroupPublicKey = FrostDkgGuard.ResolveOnChainGroupPublicKey(keyUid),
                ParticipantOrderJson = JsonConvert.SerializeObject(FrostAggregateParticipantOrderTests.DkgParticipants),
                CreatedTimestamp = TimeUtil.GetTime(),
            }));

        // ── The validator's routes ──────────────────────────────────────────────────────────────────────────

        /// <summary>The validator's FROST routes, on a node that is at the network's height.</summary>
        private HttpClient Serve()
        {
            Globals.IsChainSynced = true;
            VBTCValidatorRegistry.InvalidateCache();
            FrostSigningAuthorization.ParentConfirmations = _ => Task.FromResult<int?>(1);
            _server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            return _client = _server.CreateClient();
        }

        /// <summary>
        /// What a ceremony's leader announces for one input of a transaction, signed as a leader signs it (in full for a
        /// watched vault).
        /// </summary>
        private FrostSigningStartRequest StartOf(string keyUid, NBitcoin.Script script, string reference, BtcTx tx, V leader, int input = 0, long? timestamp = null, bool full = true)
        {
            var id = tx.GetHash().ToString();
            if (!_announced.TryGetValue(id, out var shape))
            {
                var prevouts = tx.Inputs.Select(i =>
                {
                    var coin = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N));
                    return new PinnedWithdrawalCoin { TxId = i.PrevOut.Hash.ToString(), Vout = i.PrevOut.N, ValueSats = (ulong)(coin?.ValueSats ?? 77_777), ScriptPubKeyHex = script.ToHex() };
                }).ToList();
                var spent = prevouts.Select(p => new TxOut(Money.Satoshis((long)p.ValueSats), NBitcoin.Script.FromHex(p.ScriptPubKeyHex))).ToArray();
                var precomputed = tx.PrecomputeTransactionData(spent);
                _announced[id] = shape = new FrostSigningStartRequest
                {
                    AllInputSighashes = Enumerable.Range(0, tx.Inputs.Count)
                        .Select(i => Convert.ToHexString(tx.GetSignatureHashTaproot(precomputed, new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant())
                        .ToList(),
                    UnsignedTxHex = tx.ToHex(), Prevouts = prevouts,
                };
            }
            var start = new FrostSigningStartRequest
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = keyUid, WithdrawalRequestHash = reference, LeaderAddress = leader.Address,
                MessageHash = shape.AllInputSighashes![input], InputIndex = input, InputCount = tx.Inputs.Count, AllInputSighashes = shape.AllInputSighashes.ToList(),
                UnsignedTxHex = shape.UnsignedTxHex, Prevouts = shape.Prevouts, Timestamp = timestamp ?? Now,
                SignerAddresses = FrostAggregateParticipantOrderTests.DkgParticipants.ToList(), RequiredThreshold = 51,
            };
            start.LeaderSignature = VerifiedXCore.Services.SignatureService.CreateSignature($"{start.SessionId}.{leader.Address}.{start.Timestamp}", leader.Key, leader.Pub);
            if (full) start.ContentSignature = VerifiedXCore.Services.SignatureService.CreateSignature(FrostDkgLeaderAuth.SigningStartMessage(start), leader.Key, leader.Pub);
            return start;
        }

        private async Task<(HttpStatusCode Code, string Body)> Post(FrostSigningStartRequest start)
        {
            var r = await _client!.PostAsync("/frost/sign/start", new StringContent(JsonConvert.SerializeObject(start), Encoding.UTF8, "application/json"));
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        /// <summary>The start is taken: a session is open for it here.</summary>
        private async Task<FrostSigningStartRequest> Taken(FrostSigningStartRequest start)
        {
            var (code, body) = await Post(start);
            Assert.True(code == HttpStatusCode.OK, $"{(int)code} {body}");
            Assert.True(FrostSessionStorage.SigningSessions.ContainsKey(start.SessionId));
            return start;
        }

        /// <summary>
        /// The leader's Round 2 for a session: the commitments of this validator (as it gave it for the session) and of
        /// two more of the key's holders. Answered with this validator's share.
        /// </summary>
        private async Task<(HttpStatusCode Code, string Body)> Round2(FrostSigningStartRequest start, V leader)
        {
            var round1 = await _client!.GetAsync($"/frost/sign/round1/{start.SessionId}");
            if (round1.StatusCode != HttpStatusCode.OK) return (round1.StatusCode, await round1.Content.ReadAsStringAsync());
            var nonces = new Dictionary<string, string> { [Me] = (string)JObject.Parse(await round1.Content.ReadAsStringAsync())["Nonces"]![Me]! };
            foreach (var other in new[] { "xBravo", "xCharlie" })
            {
                var (commitment, _, error) = FrostNative.SignRound1Nonces(Dkg.Value.KeyPackages[other]);
                Assert.Equal(FrostNative.SUCCESS, error);
                nonces[other] = commitment;
            }
            var body = JsonConvert.SerializeObject(nonces);
            var request = new HttpRequestMessage(HttpMethod.Post, $"/frost/sign/round2/{start.SessionId}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("X-Frost-Leader", leader.Address);
            request.Headers.TryAddWithoutValidation("X-Frost-Timestamp", start.Timestamp.ToString());
            request.Headers.TryAddWithoutValidation("X-Frost-Signature", start.LeaderSignature);
            request.Headers.TryAddWithoutValidation(FrostDkgLeaderAuth.SigningContentHeader,
                VerifiedXCore.Services.SignatureService.CreateSignature(FrostDkgLeaderAuth.SigningRound2Message(start.SessionId, body), leader.Key, leader.Pub));
            var r = await _client.SendAsync(request);
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        private async Task ShareGiven(FrostSigningStartRequest start, V leader)
        {
            var (code, body) = await Round2(start, leader);
            Assert.True(code == HttpStatusCode.OK, $"{(int)code} {body}");
            Assert.True(FrostSessionStorage.SigningSessions[start.SessionId].NonceConsumed);
        }

        /// <summary>The sessions that hold a place here now.</summary>
        private static List<SigningSession> Places() =>
            FrostSessionStorage.SigningSessions.Values.Where(s => FrostSessionStorage.SigningHoldsAPlace(s, Now)).ToList();

        /// <summary>As if the session had been opened that long ago.</summary>
        private static void Age(FrostSigningStartRequest start, int seconds) =>
            FrostSessionStorage.SigningSessions[start.SessionId].StartTimestamp -= seconds;

        // ── The tests ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One vault's requesters and a few of its key's holders, each starting every session it is given for that
        /// vault's withdrawals (two starts for an input, of one timestamp) and never asking for a share. They hold their
        /// own places and no more: another vault's withdrawal is signed, led by a holder of its key or by its requester.
        /// </summary>
        [Fact]
        public async Task AFewLeadersOfOneVaultsWithdrawals_LeaveTheRoomOfEveryOtherVault()
        {
            await Start();
            var few = Enumerable.Range(0, 5).Select(_ => NewKey()).ToList();
            var theirs = await CreateVault(Tip + 1, keyHolders: Signers.Concat(few));
            var other = await CreateVault(Tip + 1, keyHolders: Signers);
            await Deposit(theirs.Script, coins: 100, sats: 2_000);
            await Deposit(other.Script, coins: 1, sats: 150_000);

            // Four requesters, each with a withdrawal that takes 25 of the vault's coins.
            var requesters = new List<V> { Minter, Funded(), Funded(), Funded() };
            foreach (var requester in requesters.Skip(1))
                await Mine(SignedBy(Minter, requester.Address, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = theirs.Uid, FromAddress = Minter.Address, ToAddress = requester.Address, Amount = 0.0005M }));
            var withdrawals = new List<(V Requester, VfxTx Request, BtcTx Pinned)>();
            foreach (var requester in requesters)
            {
                var (request, pinned) = await Withdraw(theirs.Uid, 0.0005M, requester);
                Assert.Equal(25, pinned.Inputs.Count);
                withdrawals.Add((requester, request, pinned));
            }
            var someone = Funded();
            await Mine(SignedBy(Minter, someone.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = other.Uid, FromAddress = Minter.Address, ToAddress = someone.Address, Amount = 0.001M }));
            var honest = await Withdraw(other.Uid, 0.001M, someone);
            HoldShare(theirs.Uid);
            HoldShare(other.Uid);
            Serve();

            var at = Now;
            foreach (var (requester, request, pinned) in withdrawals)
                for (var input = 0; input < pinned.Inputs.Count; input++)
                    for (var twice = 0; twice < 2; twice++)
                        await Post(StartOf(theirs.Uid, theirs.Script, request.Hash, pinned, requester, input, at));
            foreach (var holder in few)
                for (var n = 0; n < 30; n++)
                    for (var twice = 0; twice < 2; twice++)
                        await Post(StartOf(theirs.Uid, theirs.Script, withdrawals[n / 25].Request.Hash, withdrawals[n / 25].Pinned, holder, n % 25, at));

            var theirPlaces = Places();

            // The other vault's withdrawal: led by a holder of its key, and by its requester.
            await Taken(StartOf(other.Uid, other.Script, honest.Request.Hash, honest.Pinned, Signers[0]));
            var byRequester = await Taken(StartOf(other.Uid, other.Script, honest.Request.Hash, honest.Pinned, someone));
            Assert.True(FrostSessionStorage.SigningSessions[byRequester.SessionId].LedByRequester);
            // And their own vault's withdrawals are still led by the key's other holders.
            await Taken(StartOf(theirs.Uid, theirs.Script, withdrawals[0].Request.Hash, withdrawals[0].Pinned, Signers[1]));

            // What they held: each holder its own limit, the requesters their vault's.
            Assert.All(few, holder => Assert.Equal(FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER, theirPlaces.Count(s => s.LeaderAddress == holder.Address)));
            Assert.Equal(FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS_PER_VAULT, theirPlaces.Count(s => s.LedByRequester));
            Assert.All(theirPlaces, s => Assert.Equal(theirs.Uid, s.VaultUID));
        }

        /// <summary>
        /// Every validator leading at once is what signing looks like when many payments are due: the room for the
        /// keys' holders follows the validator set. With 350 validators, 62 of them at a leader's limit (their sessions
        /// are put in the store here as the route leaves them) leave the next validator its own places, past the least
        /// room there is.
        /// </summary>
        [Fact]
        public async Task TheRoomForTheKeysHolders_FollowsTheValidatorSet()
        {
            var validators = Enumerable.Range(0, FrostSessionStorage.PlannedMaxValidators).Select(_ => NewKey()).ToList();
            BlockchainData.GetBlocks().InsertSafe(new VfxBlock
            {
                Height = 20, Hash = $"h20-{Guid.NewGuid():N}",
                Transactions = validators.Select((v, i) => new VfxTx
                {
                    TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = "r20-" + i,
                    Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.0.1", FrostPublicKey = v.Pub, IsS3C = false }),
                }).ToList(),
            });
            await Start();
            const int each = FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER;
            var busy = FrostSessionStorage.MAX_SIGNING_SESSIONS / each;                          // 62: four places short of the least room
            var vault = await CreateVault(Tip + 1, keyHolders: validators.Take(busy + 1));
            await Deposit(vault.Script, coins: each, sats: 2_000);
            var (request, pinned) = await Withdraw(vault.Uid, each * 0.00002M);
            Assert.Equal(each, pinned.Inputs.Count);
            HoldShare(vault.Uid);
            Serve();
            Assert.True(VBTCValidatorRegistry.GetActiveValidatorCount() >= validators.Count);

            foreach (var validator in validators.Take(busy))
                for (var input = 0; input < each; input++)
                {
                    var id = Guid.NewGuid().ToString();
                    FrostSessionStorage.SigningSessions[id] = new SigningSession
                    {
                        SessionId = id, LeaderAddress = validator.Address, SmartContractUID = vault.Uid, VaultUID = vault.Uid, WithdrawalRequestHash = request.Hash,
                        InputIndex = input, MessageHash = $"{validator.Address}:{input}", StartTimestamp = Now, LeaderStartTimestamp = Now, NonceSecret = "n",
                    };
                }
            Assert.Equal(busy * each, Places().Count);

            var next = validators[busy];
            for (var input = 0; input < each; input++)
                await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, next, input));
            Assert.Equal((busy + 1) * each, Places().Count);
            Assert.True(Places().Count > FrostSessionStorage.MAX_SIGNING_SESSIONS);
        }

        /// <summary>
        /// A leader signs a transaction one input after another, each session done before the next is started: it is
        /// never refused, however many inputs the transaction has, and holds one place at a time.
        /// </summary>
        [Fact]
        public async Task ALeaderSigningATransactionOfFiftyInputs_InputAfterInput_IsNeverRefused()
        {
            await Start();
            var vault = await CreateVault(Tip + 1, keyHolders: Signers);
            await Deposit(vault.Script, coins: BtcCanonicalWithdrawal.MaxInputs, sats: 2_000);
            var (request, pinned) = await Withdraw(vault.Uid, 0.001M);
            Assert.Equal(BtcCanonicalWithdrawal.MaxInputs, pinned.Inputs.Count);
            Assert.True(pinned.Inputs.Count > FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER);
            HoldShare(vault.Uid);
            Serve();

            var leader = Signers[0];
            for (var input = 0; input < pinned.Inputs.Count; input++)
            {
                var start = await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, leader, input));
                Assert.Single(Places());
                await ShareGiven(start, leader);
                Assert.Empty(Places());
            }
            Assert.Equal(pinned.Inputs.Count, FrostSessionStorage.SigningSessions.Values.Count(s => s.NonceConsumed));
        }

        /// <summary>
        /// A session's Round 2 follows its start within about a minute. One whose Round 2 has not come after two minutes
        /// gives its place up and is dropped; until then it is signed as before.
        /// </summary>
        [Fact]
        public async Task ASessionWithoutItsRound2_GivesItsPlaceUpAfterTwoMinutes()
        {
            await Start();
            var vault = await CreateVault(Tip + 1, keyHolders: Signers);
            await Deposit(vault.Script, coins: 10, sats: 2_000);
            var (request, pinned) = await Withdraw(vault.Uid, 0.0002M);
            Assert.Equal(10, pinned.Inputs.Count);
            HoldShare(vault.Uid);
            Serve();
            var leader = Signers[0];
            FrostSigningStartRequest For(int input) => StartOf(vault.Uid, vault.Script, request.Hash, pinned, leader, input);

            // A minute after its start: the share is given.
            var slow = await Taken(For(0));
            Age(slow, 60);
            await ShareGiven(slow, leader);

            // The leader's places, all open: a further start is refused.
            var open = new List<FrostSigningStartRequest>();
            for (var input = 1; input <= FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER; input++)
                open.Add(await Taken(For(input)));
            var refused = await Post(For(9));
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.Code);
            Assert.Contains("This leader already has", refused.Body);

            // Just under two minutes on they still hold them; just over, they hold nothing and are gone.
            foreach (var session in open) Age(session, 115);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(For(9))).Code);
            foreach (var session in open) Age(session, 10);
            await Taken(For(9));
            Assert.All(open, session => Assert.False(FrostSessionStorage.SigningSessions.ContainsKey(session.SessionId)));
            Assert.Equal(HttpStatusCode.NotFound, (await Round2(open[0], leader)).Code);
            Assert.True(FrostSessionStorage.SigningSessions.ContainsKey(slow.SessionId));       // its share given: kept its ten minutes
        }

        /// <summary>
        /// A leader has one session for an input here. Its next start for the input takes the place of the earlier one,
        /// also when both carry the same timestamp, and the session left is the one its Round 2 is answered in.
        /// </summary>
        [Fact]
        public async Task ALeadersNextStartForAnInput_TakesThePlaceOfItsEarlierOne_AlsoWithTheSameTimestamp()
        {
            await Start();
            var vault = await CreateVault(Tip + 1, keyHolders: Signers);
            await Deposit(vault.Script, coins: 1, sats: 150_000);
            var (request, pinned) = await Withdraw(vault.Uid, 0.001M);
            HoldShare(vault.Uid);
            Serve();
            var leader = Signers[0];
            var at = Now;

            var first = await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, leader, timestamp: at));
            var second = await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, leader, timestamp: at));
            Assert.False(FrostSessionStorage.SigningSessions.ContainsKey(first.SessionId));
            Assert.Equal(second.SessionId, Assert.Single(Places()).SessionId);

            var later = await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, leader, timestamp: at + 1));
            Assert.Equal(later.SessionId, Assert.Single(Places()).SessionId);
            await ShareGiven(later, leader);

            // Another holder's session for the same input is its own, beside the leader's.
            await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, Signers[1], timestamp: at));
            var again = await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, leader, timestamp: at + 2));
            Assert.Equal(2, Places().Count);
            Assert.Contains(Places(), s => s.SessionId == again.SessionId);
        }

        /// <summary>
        /// The two rooms are apart: as many requester-led sessions of other vaults as the keys' holders have places at
        /// the least leave a holder its place, and as many holder-led sessions leave a requester its vault's. (The other
        /// parties' sessions are put in the store here as the route leaves them.)
        /// </summary>
        [Fact]
        public async Task RequestersPlaces_AndTheKeysHoldersPlaces_AreApart()
        {
            await Start();
            var vault = await CreateVault(Tip + 1, keyHolders: Signers);
            await Deposit(vault.Script, coins: 1, sats: 150_000);
            var (request, pinned) = await Withdraw(vault.Uid, 0.001M);
            HoldShare(vault.Uid);
            Serve();
            void Others(bool requesters)
            {
                FrostSessionStorage.SigningSessions.Clear();
                for (var n = 0; n < FrostSessionStorage.MAX_SIGNING_SESSIONS; n++)
                {
                    var id = Guid.NewGuid().ToString();
                    FrostSessionStorage.SigningSessions[id] = new SigningSession
                    {
                        SessionId = id, LeaderAddress = "leader" + n, SmartContractUID = "vault" + n / 8, VaultUID = "vault" + n / 8, LedByRequester = requesters,
                        MessageHash = "hash" + n, StartTimestamp = Now, LeaderStartTimestamp = Now, NonceSecret = "n",
                    };
                }
            }

            Others(requesters: true);
            var byHolder = await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, Signers[0]));
            Assert.False(FrostSessionStorage.SigningSessions[byHolder.SessionId].LedByRequester);

            Others(requesters: false);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(StartOf(vault.Uid, vault.Script, request.Hash, pinned, Signers[1]))).Code);   // the holders' room is full
            var byRequester = await Taken(StartOf(vault.Uid, vault.Script, request.Hash, pinned, Minter));
            Assert.True(FrostSessionStorage.SigningSessions[byRequester.SessionId].LedByRequester);
        }

        /// <summary>
        /// A vault from before the chain watched vaults has its withdrawals' requesters as their only leaders, and they
        /// build the transaction themselves. Their places are their vault's: however many requesters one such vault has
        /// and whatever they start, another vault's requester has a place, and so has a holder leading a watched vault's
        /// withdrawal.
        /// </summary>
        [Fact]
        public async Task AnOlderVaultsRequesters_HoldPlacesOfTheirOwnVaultOnly()
        {
            var crowded = await CreateVault(H1 - 50, keyHolders: Signers);
            var quiet = await CreateVault(H1 - 49, keyHolders: Signers);
            await Start();
            using var electrum = new FakeElectrum(("a", true, 1M));
            var watched = await CreateVault(Tip + 1, keyHolders: Signers);
            await Deposit(watched.Script, coins: 1, sats: 150_000);
            var pinned = await Withdraw(watched.Uid, 0.001M);

            // Three requesters of one such vault, each with a transaction of as many inputs as a leader may have open.
            const int inputs = FrostSessionStorage.MAX_SIGNING_SESSIONS_PER_LEADER;
            var requesters = Enumerable.Range(0, 3).Select(_ => NewKey()).ToList();
            VfxTx Asks(string vault, V requester)
            {
                var tx = new VfxTx
                {
                    Timestamp = TimeUtil.GetTime(), FromAddress = requester.Address, ToAddress = requester.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = 0.0005M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }),
                };
                tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
                tx.Build();
                return tx;
            }
            var asked = requesters.Select(r => Asks(crowded.Uid, r)).ToList();
            await Apply(Tip + 1, asked.ToArray());
            var quietAsk = Asks(quiet.Uid, Minter);
            await Apply(Tip + 1, quietAsk);

            // A transaction as its requester builds it, from coins only Bitcoin knows about.
            BtcTx Built(NBitcoin.Script vault, int coins)
            {
                var parent = new uint256(RandomUtils.GetBytes(32));
                var tx = Network.Main.CreateTransaction();
                for (uint vout = 0; vout < coins; vout++) tx.Inputs.Add(new TxIn(new OutPoint(parent, vout)));
                tx.Outputs.Add(Money.Satoshis(50_000 - BitcoinTransactionService.WithdrawalFeeSats(coins, 10)), BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey);
                tx.Outputs.Add(Money.Satoshis(coins * 77_777L - 50_000), vault);
                return tx;
            }
            HoldShare(crowded.Uid);
            HoldShare(quiet.Uid);
            HoldShare(watched.Uid);
            Serve();

            for (var r = 0; r < requesters.Count; r++)
            {
                var built = Built(crowded.Script, inputs);
                for (var input = 0; input < inputs; input++)
                {
                    var (code, body) = await Post(StartOf(crowded.Uid, crowded.Script, asked[r].Hash, built, requesters[r], input, full: false));
                    if (r == 0) Assert.True(code == HttpStatusCode.OK, $"{(int)code} {body}");
                    else
                    {
                        Assert.True(code == HttpStatusCode.TooManyRequests, $"{(int)code} {body}");
                        Assert.Contains("for this vault", body);
                    }
                }
            }
            var places = Places();
            Assert.Equal(FrostSessionStorage.MAX_REQUESTER_LED_SIGNING_SESSIONS_PER_VAULT, places.Count);
            Assert.All(places, s => Assert.True(s.LedByRequester && s.VaultUID == crowded.Uid));

            var theQuietOnes = await Taken(StartOf(quiet.Uid, quiet.Script, quietAsk.Hash, Built(quiet.Script, 1), Minter, full: false));
            Assert.True(FrostSessionStorage.SigningSessions[theQuietOnes.SessionId].LedByRequester);
            await Taken(StartOf(watched.Uid, watched.Script, pinned.Request.Hash, pinned.Pinned, Signers[0]));
        }

        /// <summary>
        /// The rule that keeps the next room from being one a few parties fill: over the rooms declared for signing
        /// (<see cref="FrostSessionStorage.SigningRooms"/>), no party of fewer than fifty, each at its limit, reaches a
        /// room, whatever the size of the validator set; the room of the keys' holders takes every validator at its
        /// limit; and the rule the route applies gives a further party its place beside such a party. It covers the rooms
        /// of signing sessions, the ones declared so far.
        /// </summary>
        [Fact]
        public void NoSigningRoom_IsFilledByAFewPartiesAtTheirLimits()
        {
            const int few = 50;
            var rooms = FrostSessionStorage.SigningRooms;
            Assert.Equal(2, rooms.Count);
            foreach (var room in rooms)
                foreach (var validators in new[] { 0, 1, 4, 62, 63, 160, FrostSessionStorage.PlannedMaxValidators, FrostDkgAttestation.MaxParticipants, 5_000 })
                {
                    Assert.True(room.PerParty >= 1, room.Name);
                    Assert.True(few * room.PerParty < room.Places(validators), $"{few} parties fill the room {room.Name} with {validators} validators");
                }
            foreach (var validators in new[] { 63, 160, FrostSessionStorage.PlannedMaxValidators, 5_000 })
                Assert.True(rooms[0].Places(validators) >= validators * rooms[0].PerParty, $"the room of the keys' holders does not take {validators} validators");

            // As the rule applies them: fifty leaders at their limit, and fifty vaults at theirs.
            var now = TimeUtil.GetTime();
            SigningSession Open(string leader, string vault, bool requester, int n) => new()
            {
                SessionId = Guid.NewGuid().ToString("N"), LeaderAddress = leader, VaultUID = vault, LedByRequester = requester, MessageHash = $"{leader}:{vault}:{n}",
                StartTimestamp = now, SignerAddresses = new List<string>(),
            };
            var open = new List<SigningSession>();
            for (var party = 0; party < few; party++)
            {
                for (var n = 0; n < rooms[0].PerParty; n++)
                {
                    Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "leader" + party, $"leader{party}:vault:{n}", false, "vault", now, 0));
                    open.Add(Open("leader" + party, "vault", false, n));
                }
                Assert.NotNull(FrostSessionStorage.SigningStartRefusal(open, "leader" + party, "more", false, "vault", now, 0));
                for (var n = 0; n < rooms[1].PerParty; n++)
                {
                    Assert.Null(FrostSessionStorage.SigningStartRefusal(open, $"requester{party}-{n}", "x", true, "vault" + party, now, 0));
                    open.Add(Open($"requester{party}-{n}", "vault" + party, true, n));
                }
                Assert.NotNull(FrostSessionStorage.SigningStartRefusal(open, "requester-more" + party, "x", true, "vault" + party, now, 0));
            }
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "another-leader", "x", false, "another-vault", now, 0));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, "another-requester", "x", true, "another-vault", now, 0));
            Assert.Null(FrostSessionStorage.SigningStartRefusal(open, null, null, nowSeconds: now, validators: 0));
        }
    }
}
