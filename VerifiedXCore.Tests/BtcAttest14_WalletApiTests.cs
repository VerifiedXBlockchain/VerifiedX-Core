using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.BrowserWalletServices;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using FeeCalcService = VerifiedXCore.Services.FeeCalcService;
using SignatureService = VerifiedXCore.Services.SignatureService;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 14: the wallet side of a vault the chain watches. The API routes (the wallet's
    /// own, the raw ones a web wallet or an exchange signs for, and the node's browser wallet) show the attested
    /// Bitcoin, report where a withdrawal stands instead of running a signing ceremony, cancel without a vote, and raise
    /// a fee. The wallet's own records follow what the chain did. Every route is called as the node serves it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest14_WalletApiTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly VBTCController _api = new VBTCController();

        public BtcAttest14_WalletApiTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
        }

        private static long Tip => Globals.LastBlock.Height;

        private async Task<BtcTx> Deposit(NBitcoin.Script vault, long sats)
        {
            var tx = Pay(vault, sats);
            await Report(_btc.Mine(tx));
            return tx;
        }

        private async Task<VfxTx> Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            var tx = Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, 0)));
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private Task<VfxTx> Confirm(string txHex) => Report(_btc.Mine(BtcTx.Parse(txHex, Network.Main)));

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private Task<VfxTx> Request(string vault, decimal amount, V? from = null) =>
            Mine(SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }));

        private Task<VfxTx> Transfer(string vault, decimal amount) =>
            Mine(SignedBy(Minter, _holder.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = Minter.Address, ToAddress = _holder.Address, Amount = amount }));

        private static VaultWithdrawalRow Row(VfxTx request, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault))!;

        /// <summary>What a web wallet does with a raw build: signs the hash and submits it. Returns the answer and, when it was accepted, the transaction now in the mempool.</summary>
        private static async Task<(JObject Answer, VfxTx? Pooled)> SignAndSubmit(JObject built, V signer, Func<RawVBTCTxSubmission, Task<string>> send)
        {
            var hash = (string)built["Hash"]!;
            var answer = JObject.Parse(await send(new RawVBTCTxSubmission { Hash = hash, Signature = SignatureService.CreateSignature(hash, signer.Key, signer.Pub), PublicKey = signer.Pub }));
            return (answer, TransactionData.GetPool().FindOne(x => x.Hash == hash));
        }

        // ── What a vault holds ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task BalancesAndDetails_ShowTheAttestedBitcoin_OnAnyNode_AndNeverAskElectrum()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = await Deposit(vault.Script, 150_000);
            await Transfer(vault.Uid, 0.0004M);
            // Electrum would say 10 BTC. Nobody asks it about a watched vault.
            using var electrum = new FakeElectrum(("a", true, 10M));

            var owner = JObject.Parse(await _api.GetVBTCBalance(Minter.Address, vault.Uid));
            Assert.True((bool)owner["Success"]!, owner.ToString());
            Assert.True((bool)owner["Attested"]!);
            Assert.False((bool)owner["Legacy"]!);
            Assert.Equal(0.0015M, (decimal)owner["DepositAddressBalance"]!);
            Assert.False((bool)owner["DepositBalanceStale"]!);
            Assert.Equal(0.0011M, (decimal)owner["Balance"]!);
            var holder = JObject.Parse(await _api.GetVBTCBalance(_holder.Address, vault.Uid));
            Assert.Equal(0.0004M, (decimal)holder["Balance"]!);
            Assert.True((bool)holder["Attested"]!);

            Assert.Empty(electrum.Asked);
            // (The owner's other vault, created before attestation, is still looked up the earlier way.)
            var all = JObject.Parse(await _api.GetAllVBTCBalances(Minter.Address));
            var mine = ((JArray)all["Contracts"]!).Single(c => (string)c["SmartContractUID"]! == vault.Uid);
            Assert.Equal(0.0011M, (decimal)mine["Balance"]!);
            Assert.True((bool)mine["Attested"]!);

            var listed = ((JArray)JObject.Parse(await _api.GetContractList(Minter.Address))["Contracts"]!).Single(c => (string)c["SmartContractUID"]! == vault.Uid);
            Assert.True((bool)listed["Attested"]!);
            Assert.Equal(0.0015M, (decimal)listed["Balance"]!);

            var details = JObject.Parse(await _api.GetContractDetails(vault.Uid));
            Assert.True((bool)details["Vault"]!["Attested"]!);
            Assert.Equal(150_000, (long)details["Bitcoin"]!["HeldSats"]!);

            var bitcoin = JObject.Parse(await _api.GetVaultBitcoin(vault.Uid));
            Assert.True((bool)bitcoin["Success"]!);
            Assert.Equal(vault.Address, (string)bitcoin["Bitcoin"]!["DepositAddress"]!);
            Assert.Equal(3, (int)bitcoin["Bitcoin"]!["ConfirmationsRequired"]!);
            var coin = Assert.Single((JArray)bitcoin["Bitcoin"]!["Coins"]!);
            Assert.Equal(Outpoint(deposit, 0), (string)coin["Outpoint"]!);
            Assert.Equal("Unspent", (string)coin["Status"]!);

            var status = JObject.Parse(await _api.GetBitcoinAttestationStatus())["Attestation"]!;
            Assert.True((bool)status["Started"]!);
            Assert.True((bool)status["Recording"]!);
            Assert.False((bool)status["Enforced"]!);
            Assert.Equal(H1, (long)status["RecordHeight"]!);
            Assert.Equal(JTokenType.Null, status["EnforceHeight"]!.Type);       // not set yet
            Assert.Equal(_btc.Tip, (long)status["BitcoinTipHeight"]!);
            Assert.Equal(4, (int)status["Signers"]!);
            Assert.Equal(3, (int)status["SignaturesRequired"]!);

            // A vault created before attestation says so, and names its deadline once the height is set.
            var legacy = JObject.Parse(await _api.GetVBTCBalance(Minter.Address, older.Uid));
            Assert.False((bool)legacy["Attested"]!);
            Assert.True((bool)legacy["Legacy"]!);
            Assert.False((bool)legacy["Frozen"]!);
            Assert.Contains("created before Bitcoin attestation", (string)legacy["VaultNote"]!);
            Assert.False((bool)JObject.Parse(await _api.GetVaultBitcoin(older.Uid))["Success"]!);
            Globals.BitcoinAttestationHeight = Tip + 500; // restored by the base class
            legacy = JObject.Parse(await _api.GetVBTCBalance(Minter.Address, older.Uid));
            Assert.Equal(Tip + 500, (long)legacy["FreezeHeight"]!);
            Assert.Contains("frozen from block " + (Tip + 500), (string)legacy["VaultNote"]!);
            Globals.BitcoinAttestationHeight = Tip + 1;
            legacy = JObject.Parse(await _api.GetVBTCBalance(Minter.Address, older.Uid));
            Assert.True((bool)legacy["Frozen"]!);
            // The watched vault carries no such notice.
            owner = JObject.Parse(await _api.GetVBTCBalance(Minter.Address, vault.Uid));
            Assert.False((bool)owner["Legacy"]!);
            Assert.Equal(JTokenType.Null, owner["FreezeHeight"]!.Type);
        }

        // ── Completing ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheCompletionRoutes_SayWhereAWithdrawalStands_AndSignNothing()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var request = await Request(vault.Uid, 0.001M);
            var waiting = await Request(vault.Uid, 0.0002M);           // the vault's only coin is held by the first
            var pin = Assert.Single(Row(request, vault.Uid).Pins);

            async Task<JObject[]> EveryCompletionRoute(string hash) => new[]
            {
                JObject.Parse(await _api.CompleteWithdrawal(new VBTCWithdrawalCompletePayload { SmartContractUID = vault.Uid, WithdrawalRequestHash = hash })),
                JObject.Parse(await _api.PrepareCompleteWithdrawalRaw(new PrepareCompleteWithdrawalRawPayload { OwnerAddress = Minter.Address, SmartContractUID = vault.Uid, WithdrawalRequestHash = hash })),
                JObject.Parse(await _api.ExecuteCompleteWithdrawalRaw(new ExecuteCompleteWithdrawalRawPayload
                {
                    OwnerAddress = Minter.Address, SmartContractUID = vault.Uid, WithdrawalRequestHash = hash, SessionId = "s",
                    StartSignature = "x", ShareDistributionSignature = "y",
                })),
                JObject.Parse(await _api.GetRawCompleteWithdrawalTxData(new RawCompleteWithdrawalTxPayload
                {
                    SmartContractUID = vault.Uid, FromAddress = Minter.Address, WithdrawalRequestHash = hash, BTCTransactionHash = new string('a', 64),
                    Amount = 0.001M, BTCDestination = BtcDest,
                })),
            };

            // Fixed, not yet confirmed: "it is being paid", with the transaction. Not an error, and nothing was signed.
            foreach (var r in await EveryCompletionRoute(request.Hash))
            {
                Assert.True((bool)r["Success"]!, r.ToString());
                Assert.True((bool)r["Attested"]!);
                Assert.True((bool)r["NothingToDo"]!);
                Assert.Equal("Awaiting_BTC_Confirmation", (string)r["Status"]!);
                Assert.Equal(pin.Txid, (string)r["BTCTransactionHash"]!);
                Assert.StartsWith(VBTCService.AwaitingConfirmationMarker, (string)r["Message"]!);
                Assert.Null(r["SignedBTCTxHex"]);
                Assert.Null(r["Hash"]);
            }
            foreach (var r in await EveryCompletionRoute(waiting.Hash))
            {
                Assert.True((bool)r["Success"]!);
                Assert.Equal("Awaiting_BTC_Transaction", (string)r["Status"]!);
                Assert.StartsWith(BtcVaultView.WaitingForPinMarker, (string)r["Message"]!);
                Assert.Equal(JTokenType.Null, r["BTCTransactionHash"]!.Type);
            }
            foreach (var r in await EveryCompletionRoute(new string('f', 64)))
            {
                Assert.False((bool)r["Success"]!);
                Assert.Equal("Unknown", (string)r["Status"]!);
            }

            // The service every other caller goes through gives the same answer, in its usual shape.
            var direct = await VBTCService.CompleteWithdrawal(vault.Uid, request.Hash);
            Assert.False(direct.Success);
            Assert.Equal(pin.Txid, direct.BTCTxHash);
            Assert.True(BtcVaultView.IsInTheChainsHands(direct.ErrorMessage));
            var multi = JObject.Parse(await _api.CompleteWithdrawalMulti(new VBTCWithdrawalCompleteMultiPayload { WithdrawalRequestHash = request.Hash }));
            Assert.StartsWith(VBTCService.AwaitingConfirmationMarker, (string)Assert.Single((JArray)multi["Results"]!)["ErrorMessage"]!);

            // The status routes.
            var view = JObject.Parse(await _api.GetWithdrawalPin(vault.Uid, request.Hash));
            Assert.True((bool)view["Success"]!);
            Assert.Equal("Pinned", (string)view["Withdrawal"]!["State"]!);
            Assert.Equal(pin.Txid, (string)view["Withdrawal"]!["BitcoinTxid"]!);
            Assert.True((bool)view["Withdrawal"]!["CanRaiseFee"]!);
            Assert.False((bool)view["Withdrawal"]!["CanCancel"]!);
            Assert.Equal(pin.TxHex, (string)Assert.Single((JArray)view["Withdrawal"]!["Transactions"]!)["UnsignedTxHex"]!);
            view = JObject.Parse(await _api.GetWithdrawalPin(vault.Uid, waiting.Hash));
            Assert.Equal("Waiting", (string)view["Withdrawal"]!["State"]!);
            Assert.True((bool)view["Withdrawal"]!["CanCancel"]!);
            Assert.Equal(waiting.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1, (long)view["Withdrawal"]!["ExpiresAtVfxHeight"]!);
            Assert.False((bool)JObject.Parse(await _api.GetWithdrawalPin(vault.Uid, new string('f', 64)))["Success"]!);
            var open = JObject.Parse(await _api.GetWithdrawalStatus(vault.Uid));
            Assert.True((bool)open["Attested"]!);
            Assert.True((bool)open["HasActiveWithdrawal"]!);
            Assert.Equal(2, ((JArray)open["Bitcoin"]!["OpenWithdrawals"]!).Count);
            // Escrow is listed as open, never as "expired, cancel to get it back": consensus closes what it must.
            var balance = JObject.Parse(await _api.GetVBTCBalance(Minter.Address, vault.Uid));
            Assert.Equal(0.0012M, (decimal)balance["EscrowedWithdrawalAmount"]!);
            Assert.Equal(0M, (decimal)balance["ExpiredEscrowAmount"]!);
            Assert.All((JArray)balance["EscrowedWithdrawals"]!, w => { Assert.True((bool)w["Attested"]!); Assert.False((bool)w["Expired"]!); });

            // Bitcoin confirms it: completed, with the transaction, and in the history.
            var report = await Confirm(pin.TxHex);
            foreach (var r in await EveryCompletionRoute(request.Hash))
            {
                Assert.True((bool)r["Success"]!);
                Assert.Equal("Completed", (string)r["Status"]!);
                Assert.Equal(pin.Txid, (string)r["BTCTransactionHash"]!);
                Assert.Equal(report.Hash, (string)r["VFXTransactionHash"]!);
            }
            direct = await VBTCService.CompleteWithdrawal(vault.Uid, request.Hash);
            Assert.True(direct.Success);
            Assert.Equal(pin.Txid, direct.BTCTxHash);
            var history = Assert.Single((JArray)JObject.Parse(await _api.GetWithdrawalHistory(vault.Uid))["WithdrawalHistory"]!);
            Assert.Equal(request.Hash, (string)history["RequestHash"]!);
            Assert.Equal(pin.Txid, (string)history["BTCTransactionHash"]!);
            Assert.Equal(report.Hash, (string)history["CompletionHash"]!);
        }

        // ── Raising the fee and cancelling, as a web wallet does ────────────────────────────────────────────

        [Fact]
        public async Task ARawFeeBump_IsBuilt_SignedOffline_Submitted_AndMined()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var request = await Request(vault.Uid, 0.001M);

            Task<string> Build(string scUID, string requester, string hash, long rate) =>
                _api.GetRawBumpWithdrawalFeeTxData(new VBTCWithdrawalFeeBumpPayload { SmartContractUID = scUID, RequestorAddress = requester, WithdrawalRequestHash = hash, FeeRate = rate });

            // Refused before the wallet signs anything, with the reason block validation would give.
            var refused = JObject.Parse(await Build(vault.Uid, Minter.Address, request.Hash, 10));
            Assert.False((bool)refused["Success"]!);
            Assert.Contains("must be above the 10 sat/vB", (string)refused["Message"]!);
            Assert.Equal("Pinned", (string)refused["Withdrawal"]!["State"]!);
            Assert.Contains("Only the requester", (string)JObject.Parse(await Build(vault.Uid, _holder.Address, request.Hash, 25))["Message"]!);
            Assert.Contains("vault the chain watches", (string)JObject.Parse(await Build(older.Uid, Minter.Address, request.Hash, 25))["Message"]!);
            Assert.False((bool)JObject.Parse(await Build(vault.Uid, Minter.Address, request.Hash, 0))["Success"]!);

            var built = JObject.Parse(await Build(vault.Uid, Minter.Address, request.Hash, 25));
            Assert.True((bool)built["Success"]!, built.ToString());
            Assert.Equal("VBTC_V2_COIN_PIN", (string)built["TransactionType"]!);
            Assert.Equal(44, (int)built["TransactionTypeId"]!);
            Assert.Equal(25, (long)JObject.Parse((string)built["Data"]!)["FeeRate"]!);

            // A wrong signature does not destroy the build; the right one sends it.
            var (bad, _) = await SignAndSubmit(built, _holder, _api.SendRawBumpWithdrawalFeeTx);
            Assert.False((bool)bad["Success"]!);
            var (sent, pooled) = await SignAndSubmit(built, Minter, _api.SendRawBumpWithdrawalFeeTx);
            Assert.True((bool)sent["Success"]!, sent.ToString());
            Assert.NotNull(pooled);
            Assert.False((bool)JObject.Parse(await _api.SendRawBumpWithdrawalFeeTx(new RawVBTCTxSubmission { Hash = pooled!.Hash, Signature = "s", PublicKey = "p" }))["Success"]!); // already submitted

            await Mine(pooled);
            var view = JObject.Parse(await _api.GetWithdrawalPin(vault.Uid, request.Hash))["Withdrawal"]!;
            Assert.Equal(25, (long)view["FeeRate"]!);
            var transactions = (JArray)view["Transactions"]!;
            Assert.Equal(2, transactions.Count);
            Assert.True((bool)transactions[0]["Superseded"]!);
            Assert.Equal((string)view["BitcoinTxid"]!, (string)transactions[1]["Txid"]!);

            // The wallet's own copy of the transaction (saved when it was sent) is marked mined by the wallet's block
            // passes, as the node runs them for a transaction from and to one of its accounts.
            var saved = TransactionData.GetAll().FindOne(x => x.Hash == pooled.Hash);
            Assert.NotNull(saved);
            Assert.NotEqual(TransactionStatus.Success, saved.TransactionStatus);
            var account = new Account { Address = Minter.Address };
            await BlockTransactionValidatorService.ProcessOutgoingTransaction(pooled, account, Tip);
            await BlockTransactionValidatorService.ProcessIncomingTransactions(pooled, account, Tip);
            var copies = TransactionData.GetAll().Find(x => x.Hash == pooled.Hash).ToList();
            Assert.NotEmpty(copies);
            Assert.All(copies, c => Assert.Equal(TransactionStatus.Success, c.TransactionStatus));
        }

        [Fact]
        public async Task ARawCancel_IsRefusedBeforeSigningOnceTheTransactionIsFixed_AndNeedsNoVote()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            var pinned = await Request(vault.Uid, 0.001M);
            var waiting = await Request(vault.Uid, 0.0002M);

            Task<string> Build(string hash) => _api.GetRawCancelWithdrawalTxData(new VBTCWithdrawalCancelTxPayload { SmartContractUID = vault.Uid, RequestorAddress = Minter.Address, WithdrawalRequestHash = hash });

            var refused = JObject.Parse(await Build(pinned.Hash));
            Assert.False((bool)refused["Success"]!);
            Assert.True((bool)refused["Attested"]!);
            Assert.Contains("cannot be cancelled", (string)refused["Message"]!);

            var built = JObject.Parse(await Build(waiting.Hash));
            Assert.True((bool)built["Success"]!, built.ToString());
            Assert.Contains("no vote is held", (string)built["Message"]!);
            var (sent, pooled) = await SignAndSubmit(built, Minter, _api.SendRawCancelWithdrawalTx);
            Assert.True((bool)sent["Success"]!, sent.ToString());
            await Mine(pooled!);

            Assert.Equal(VaultWithdrawalState.Cancelled, Row(waiting, vault.Uid).State);
            Assert.Equal(0.0005M, (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault.Uid, Minter.Address)).availableBalance); // refunded at once
            var status = JObject.Parse(await _api.GetCancellationStatus(vault.Uid, waiting.Hash));
            Assert.True((bool)status["Attested"]!);
            Assert.Equal("Cancelled", (string)status["Withdrawal"]!["State"]!);
            Assert.Empty((JArray)status["Cancellations"]!);
        }

        // ── The wallet's own records and the node's browser wallet ──────────────────────────────────────────

        [Fact]
        public async Task TheWalletsRecordOfAVault_FollowsWhatTheChainDid()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            VBTCContractV2.SaveContract(new VBTCContractV2
            {
                SmartContractUID = vault.Uid, OwnerAddress = Minter.Address, DepositAddress = vault.Address,
                ValidatorAddressesSnapshot = new List<string>(), WithdrawalHistory = new List<VBTCWithdrawalHistory>(),
            });

            var paid = await Request(vault.Uid, 0.001M);
            Assert.Equal(paid.Hash, VBTCContractV2.GetContract(vault.Uid)!.ActiveWithdrawalRequestHash);
            var report = await Confirm(Row(paid, vault.Uid).Pins[0].TxHex);
            var local = VBTCContractV2.GetContract(vault.Uid)!;
            Assert.Null(local.ActiveWithdrawalRequestHash);
            Assert.Equal(VBTCWithdrawalStatus.Completed, local.WithdrawalStatus);
            var entry = Assert.Single(local.WithdrawalHistory);
            Assert.Equal(paid.Hash, entry.RequestHash);
            Assert.Equal(Row(paid, vault.Uid).SettledTxid, entry.BTCTransactionHash);
            Assert.Equal(report.Hash, entry.CompletionHash);

            // One that is never pinned and expires: the record stops showing it, and lists nothing for it.
            var held = await Request(vault.Uid, 0.0003M);                 // takes the change coin
            var never = await Request(vault.Uid, 0.0001M);                // waits
            Assert.Equal(VaultWithdrawalState.Waiting, Row(never, vault.Uid).State);
            Assert.Equal(never.Hash, VBTCContractV2.GetContract(vault.Uid)!.ActiveWithdrawalRequestHash);
            await Apply(never.Height + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            Assert.Equal(VaultWithdrawalState.Expired, Row(never, vault.Uid).State);
            local = VBTCContractV2.GetContract(vault.Uid)!;
            Assert.Null(local.ActiveWithdrawalRequestHash);
            Assert.Equal(VBTCWithdrawalStatus.None, local.WithdrawalStatus);
            Assert.Single(local.WithdrawalHistory);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(held, vault.Uid).State);
        }

        [Fact]
        public async Task TheBrowserWallet_ListsAWatchedVaultsWithdrawals_AndOffersNoCompleteButton()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            await Transfer(vault.Uid, 0.0003M);
            var request = await Request(vault.Uid, 0.001M);
            var holders = await Request(vault.Uid, 0.0001M, from: _holder);   // waits behind the owner's

            var card = JArray.FromObject(WalletVbtcService.GetVBTCContracts(Minter.Address)).Single(c => (string)c["scUID"]! == vault.Uid);
            Assert.True((bool)card["attested"]!);
            Assert.False((bool)card["frozen"]!);
            Assert.Equal(vault.Address, (string)card["depositAddress"]!);        // with no local record of the vault on this node
            Assert.Equal(0.0002M, (decimal)card["balance"]!);                    // 0.0015 held - 0.0003 sent - 0.001 requested
            var open = Assert.Single((JArray)card["openWithdrawals"]!);         // this address's own, not the holder's
            Assert.Equal(request.Hash, (string)open["requestHash"]!);
            Assert.Equal("awaiting Bitcoin", (string)open["state"]!);
            Assert.True((bool)open["canRaiseFee"]!);
            Assert.False((bool)open["canCancel"]!);
            Assert.Equal(Row(request, vault.Uid).Pins[0].Txid, (string)open["bitcoinTxid"]!);

            var theirs = Assert.Single((JArray)JArray.FromObject(WalletVbtcService.GetVBTCContracts(_holder.Address)).Single(c => (string)c["scUID"]! == vault.Uid)["openWithdrawals"]!);
            Assert.Equal(holders.Hash, (string)theirs["requestHash"]!);
            Assert.True((bool)theirs["canCancel"]!);
            Assert.Equal("waiting for coins", (string)theirs["state"]!);

            // "Complete" from the page: told it is being paid, with the transaction; no ceremony.
            var (success, message, vfxTxHash, btcTxHash) = await WalletVbtcService.CompleteWithdrawal(vault.Uid, request.Hash);
            Assert.True(success);
            Assert.DoesNotContain("[", message);
            Assert.Contains("Nothing needs to be done", message);
            Assert.Null(vfxTxHash);
            Assert.Equal(Row(request, vault.Uid).Pins[0].Txid, btcTxHash);
            Assert.NotNull(WalletVbtcService.GetWithdrawalPin(vault.Uid, request.Hash));
            Assert.Null(WalletVbtcService.GetWithdrawalPin(vault.Uid, new string('f', 64)));
            Assert.True((bool)JObject.FromObject(WalletVbtcService.GetWithdrawStatus(vault.Uid))["attested"]!);

            // The page itself: no Complete button for a watched vault, its withdrawals listed with Raise fee and Cancel.
            var page = BrowserUIService.GetHtml();
            Assert.Contains("var canComplete=!c.attested&&!c.frozen&&c.withdrawalStatus==='Requested';", page);
            Assert.Contains("/wallet/api/vbtc/withdraw/bumpfee", page);
            Assert.Contains("function vbtcOpenWithdrawals(c)", page);
        }
    }
}
