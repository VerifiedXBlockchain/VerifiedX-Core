using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using BtcBlock = NBitcoin.Block;
using BtcTx = NBitcoin.Transaction;
using VfxBlock = VerifiedXCore.Models.Block;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Shared ground for the Bitcoin attestation tests: a fresh state, vBTC validators registered on chain (four holding
    /// the validator balance, one not), and helpers that go through the node's own entry points:
    /// TransactionValidatorService.VerifyTX to judge a transaction and StateData.UpdateTreis to apply a block.
    /// </summary>
    public abstract class BtcAttestTestBase : IDisposable
    {
        /// <summary>The recording height (H1) these tests run with.</summary>
        protected const long H1 = 100;

        protected sealed record V(PrivateKey Key, string Pub, string Address);

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly VfxBlock _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorTestNet = Globals.IsTestNet;
        private readonly long _priorObserve = Globals.BitcoinAttestationObserveHeight;
        private readonly long _priorRecord = Globals.BitcoinAttestationRecordHeight;
        private readonly long _priorEnforce = Globals.BitcoinAttestationHeight;
        private readonly long _priorCheckpointHeight = Globals.BitcoinCheckpointHeightMainnet;
        private readonly string _priorCheckpointHeaders = Globals.BitcoinCheckpointHeadersMainnet;
        // The withdrawal rules that both networks are already past. The recording height is always above them.
        private readonly long[] _priorWithdrawalGates =
        {
            Globals.WithdrawalEscrowHeight, Globals.V2WithdrawalExpiryFixHeight, Globals.V2WithdrawalOwnerAddBackFixHeight,
            Globals.VbtcWithdrawalConcurrencyHeight, Globals.VbtcCancellationVoteRulesHeight, Globals.WithdrawalUniqueIdRulesHeight,
        };

        /// <summary>Four registered validators that hold the validator balance.</summary>
        protected readonly List<V> Signers = Enumerable.Range(0, 4).Select(_ => NewKey()).ToList();
        /// <summary>Registered and active, but without the validator balance.</summary>
        protected readonly V Unfunded = NewKey();
        protected readonly V Minter = NewKey();

        protected BtcAttestTestBase(bool realMainnetRules)
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"btcattest_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            Globals.IsTestNet = false; // Bitcoin mainnet addresses and transactions
            Globals.BitcoinAttestationObserveHeight = H1;
            Globals.BitcoinAttestationRecordHeight = H1;
            BtcChainRules.OverrideForTests = realMainnetRules ? null : BtcChainRules.Regtest;
            BtcAttestRules.DepthOverride = realMainnetRules ? null : 3;
            Globals.WithdrawalEscrowHeight = 1;
            Globals.V2WithdrawalExpiryFixHeight = 1;
            Globals.V2WithdrawalOwnerAddBackFixHeight = 1;
            Globals.VbtcWithdrawalConcurrencyHeight = 1;
            Globals.VbtcCancellationVoteRulesHeight = 1;
            Globals.WithdrawalUniqueIdRulesHeight = 1;
            StateWriteContext.Clear();
            DbContext.Initialize();
            Globals.LastBlock = new VfxBlock { Height = H1 - 2 }; // a chain two blocks short of the recording height

            long h = 10;
            foreach (var v in Signers.Append(Unfunded))
            {
                BlockchainData.GetBlocks().InsertSafe(new VfxBlock
                {
                    Height = h, Hash = $"h{h}-{Guid.NewGuid():N}",
                    Transactions = new List<VfxTx>
                    {
                        new VfxTx
                        {
                            TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = "r" + h,
                            Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.0.1", FrostPublicKey = v.Pub, IsS3C = false }),
                        },
                    },
                });
                h++;
            }
            foreach (var v in Signers)
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = 5_000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Unfunded.Address, Balance = 4_999M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Minter.Address, Balance = 100M, Nonce = 0 });
        }

        public virtual void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            StateWriteContext.Clear();
            BtcChainRules.OverrideForTests = null;
            BtcAttestRules.DepthOverride = null;
            Globals.BitcoinAttestationObserveHeight = _priorObserve;
            Globals.BitcoinAttestationRecordHeight = _priorRecord;
            Globals.BitcoinAttestationHeight = _priorEnforce;
            Globals.BitcoinCheckpointHeightMainnet = _priorCheckpointHeight;
            Globals.BitcoinCheckpointHeadersMainnet = _priorCheckpointHeaders;
            Globals.WithdrawalEscrowHeight = _priorWithdrawalGates[0];
            Globals.V2WithdrawalExpiryFixHeight = _priorWithdrawalGates[1];
            Globals.V2WithdrawalOwnerAddBackFixHeight = _priorWithdrawalGates[2];
            Globals.VbtcWithdrawalConcurrencyHeight = _priorWithdrawalGates[3];
            Globals.VbtcCancellationVoteRulesHeight = _priorWithdrawalGates[4];
            Globals.WithdrawalUniqueIdRulesHeight = _priorWithdrawalGates[5];
            Globals.IsTestNet = _priorTestNet;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        protected static V NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return new V(key, pub, AccountData.GetHumanAddress(pub));
        }

        protected static string NewTaprootAddress() =>
            FrostDkgAttestation.DeriveTaprootAddress(Convert.ToHexString(new NBitcoin.Key().PubKey.ToBytes()[1..]).ToLowerInvariant(), Network.Main)!;

        protected static NBitcoin.Script ScriptOf(string address) => BitcoinAddress.Create(address, Network.Main).ScriptPubKey;

        protected static string Outpoint(BtcTx tx, long vout) => BtcAttestStore.OutpointKey(tx.GetHash().ToString(), vout);

        // ── The node's entry points ─────────────────────────────────────────────────────────────────────────

        /// <summary>Applies a block the way the node does, and moves the tip to it.</summary>
        protected static async Task Apply(long height, params VfxTx[] txs)
        {
            var block = new VfxBlock { Height = height, Hash = $"b{height}", StateRoot = "root" + height, Timestamp = TimeUtil.GetTime(), Transactions = txs.ToList() };
            Assert.True(await StateData.UpdateTreis(block));
            Globals.LastBlock = block;
        }

        /// <summary>Block validation's verdict on a transaction of the block at <paramref name="height"/>.</summary>
        protected static async Task<(bool Ok, string Why)> Verify(VfxTx tx, long height)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);
            return (ok, why);
        }

        /// <summary>The block at the recording height: the Bitcoin view starts and the first signer set is stored.</summary>
        protected static Task Start() => Apply(H1);

        /// <summary>Creates a vBTC V2 vault on chain (a contract creation applied in a block) and returns its identity.</summary>
        protected async Task<(string Uid, string Address, NBitcoin.Script Script)> CreateVault(long vfxHeight, string? depositAddress = null)
        {
            var address = depositAddress ?? NewTaprootAddress();
            var uid = FrostDkgAttestation.NewContractUid();
            var body = VbtcTestContracts.VaultContractData(uid, Minter.Address, address);
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = Minter.Address, ToAddress = Minter.Address, Amount = 0M, Fee = 0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = uid, Data = body, MD5List = "NA" } }),
            };
            tx.Build();
            await Apply(vfxHeight, tx);
            return (uid, address, ScriptOf(address));
        }

        // ── Reports ─────────────────────────────────────────────────────────────────────────────────────────

        protected static BtcReportSignature Sign(V validator, BtcReport report) => new()
        {
            ValidatorAddress = validator.Address,
            Signature = SignatureService.CreateSignature(BtcReport.SigningMessage(report.Hash()), validator.Key, validator.Pub),
        };

        /// <summary>Signs a report (by three of the four funded validators unless told otherwise) and wraps it in its transaction.</summary>
        protected VfxTx Signed(BtcReport report, IEnumerable<V>? signers = null)
        {
            report.Signatures = (signers ?? Signers.Take(3)).Select(v => Sign(v, report)).ToList();
            return Tx(report);
        }

        protected static VfxTx Tx(BtcReport report) => BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REPORT, report.ToCanonicalJson());

        /// <summary>The event for the transaction at <paramref name="position"/> of a block, with its proof.</summary>
        protected static BtcReportEvent EventFor(BtcBlock block, int position)
        {
            var txids = block.Transactions.Select(t => t.GetHash().ToString()).ToList();
            return new BtcReportEvent { Position = position, TxHex = BtcTxInclusion.ToStrippedHex(block.Transactions[position]), Branch = BtcMerkle.BranchFor(txids, position) };
        }

        // ── A Bitcoin chain made for the test (regtest proof-of-work) ───────────────────────────────────────

        /// <summary>
        /// A chain of real Bitcoin blocks at regtest difficulty: real transactions, real Merkle roots, headers that meet
        /// their target. Built with NBitcoin, checked by the node's own code.
        /// </summary>
        protected sealed class Btc
        {
            private readonly long _firstHeight;
            private readonly List<BtcBlock> _blocks = new();
            private readonly List<BtcHeader> _headers = new();
            private uint _time;

            public Btc(long firstHeight, uint time = 1_760_000_000)
            {
                _firstHeight = firstHeight;
                _time = time;
            }

            public long Tip => _firstHeight + _blocks.Count - 1;
            public BtcBlock Block(long height) => _blocks[(int)(height - _firstHeight)];
            public BtcHeader Header(long height) => _headers[(int)(height - _firstHeight)];
            public List<string> HeaderHexes(long from, long to) =>
                Enumerable.Range(0, (int)Math.Max(0, to - from + 1)).Select(i => Header(from + i).Hex).ToList();

            /// <summary>A second chain that shares this one's blocks up to <paramref name="height"/> and differs after it.</summary>
            public Btc ForkAfter(long height)
            {
                var fork = new Btc(_firstHeight, 0);
                for (var h = _firstHeight; h <= height; h++)
                {
                    fork._blocks.Add(Block(h));
                    fork._headers.Add(Header(h));
                }
                fork._time = Header(height).Time;
                return fork;
            }

            /// <summary>Mines the next block with these transactions after its coinbase; returns its height.</summary>
            public long Mine(params BtcTx[] txs) => MineWithCoinbase(null, txs);

            /// <summary>As <see cref="Mine"/>, with further outputs on the coinbase transaction (a miner paying out directly).</summary>
            public long MineWithCoinbase(IEnumerable<(NBitcoin.Script Script, long Sats)>? coinbaseOutputs, params BtcTx[] txs)
            {
                var height = _firstHeight + _blocks.Count;
                var block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
                block.Header.Version = 0x20000000;
                block.Header.HashPrevBlock = _headers.Count == 0 ? uint256.Zero : uint256.Parse(_headers[^1].Hash);
                _time += 600;
                block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(_time);
                block.Header.Bits = new Target(0x207fffffu);

                var coinbase = Network.Main.CreateTransaction();
                coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue), new NBitcoin.Script(Op.GetPushOp(height), Op.GetPushOp(RandomUtils.GetBytes(8)))));
                coinbase.Outputs.Add(Money.Coins(3.125m), new NBitcoin.Key().PubKey.Hash.ScriptPubKey);
                foreach (var (script, sats) in coinbaseOutputs ?? Array.Empty<(NBitcoin.Script, long)>())
                    coinbase.Outputs.Add(Money.Satoshis(sats), script);
                block.Transactions.Add(coinbase);
                foreach (var tx in txs) block.Transactions.Add(tx);
                block.UpdateMerkleRoot();

                BtcHeader? header;
                while (!BtcHeader.TryParse(Convert.ToHexString(block.Header.ToBytes()).ToLowerInvariant(), out header) || !header!.MeetsOwnTarget())
                    block.Header.Nonce++;
                _blocks.Add(block);
                _headers.Add(header!);
                return height;
            }

            public void MineEmpty(int count)
            {
                for (var i = 0; i < count; i++) Mine();
            }
        }

        /// <summary>A Bitcoin transaction spending <paramref name="spends"/> (or a coin from nowhere) and paying these outputs.</summary>
        protected static BtcTx Pay(IEnumerable<OutPoint>? spends, params (NBitcoin.Script Script, long Sats)[] outputs)
        {
            var tx = Network.Main.CreateTransaction();
            foreach (var prev in spends ?? new[] { new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0) })
                tx.Inputs.Add(new TxIn(prev));
            foreach (var (script, sats) in outputs)
                tx.Outputs.Add(Money.Satoshis(sats), script);
            return tx;
        }

        protected static BtcTx Pay(NBitcoin.Script script, long sats) => Pay(null, (script, sats));

        protected static NBitcoin.Script Elsewhere() => new NBitcoin.Key().PubKey.Hash.ScriptPubKey;
    }
}
