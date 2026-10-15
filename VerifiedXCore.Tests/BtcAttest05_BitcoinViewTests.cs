using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Attestation.Net;
using Xunit;
using BtcBlock = NBitcoin.Block;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 5: the node's own view of Bitcoin (plan 2.3 and 2.5). The header chain a
    /// validator follows, the check a fetched block must pass, and the light client that keeps both, driven here by
    /// sources the test controls: an honest one, one that lies, one on another chain.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest05_BitcoinViewTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const long First = Checkpoint + 1;
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly string _blockDir = Path.Combine(Path.GetTempPath(), $"btcblocks_{Guid.NewGuid():N}");

        public BtcAttest05_BitcoinViewTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
        }

        public override void Dispose()
        {
            base.Dispose();
            try { Directory.Delete(_blockDir, recursive: true); } catch { }
        }

        private BtcHeaderChain NewChain()
        {
            Assert.True(BtcCheckpoint.TryParse(Checkpoint, string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint)), out var checkpoint, out var why), why);
            return new BtcHeaderChain(checkpoint!, BtcChainRules.Regtest);
        }

        private static List<BtcHeader> Headers(Btc from, long first, long last) =>
            Enumerable.Range(0, (int)(last - first + 1)).Select(i => from.Header(first + i)).ToList();

        // ── The header chain ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheHeaderChain_FollowsTheValidBranchWithTheMostWork()
        {
            var chain = NewChain();
            Assert.Equal(Checkpoint, chain.TipHeight);

            _btc.MineEmpty(5);
            Assert.Equal(BtcHeaderAdd.Extended, chain.Add(Headers(_btc, First, First + 4), out _));
            Assert.Equal(First + 4, chain.TipHeight);
            Assert.Equal(BtcHeaderAdd.Nothing, chain.Add(Headers(_btc, First, First + 4), out _));
            Assert.Equal(BtcHeaderAdd.Nothing, chain.Add(new List<BtcHeader>(), out _));

            // A run that starts inside what is held and goes on.
            _btc.MineEmpty(1);
            Assert.Equal(BtcHeaderAdd.Extended, chain.Add(Headers(_btc, First + 2, First + 5), out _));
            Assert.Equal(First + 5, chain.TipHeight);

            // A branch of equal work changes nothing.
            var fork = _btc.ForkAfter(First + 2);
            fork.MineEmpty(3);
            Assert.Equal(BtcHeaderAdd.SideBranch, chain.Add(Headers(fork, First + 3, First + 5), out _));
            Assert.Equal(_btc.Header(First + 5).Hash, chain.Tip.Hash);

            // One more block of work and it becomes the best chain.
            fork.MineEmpty(1);
            Assert.Equal(BtcHeaderAdd.Reorganised, chain.Add(Headers(fork, First + 3, First + 6), out _));
            Assert.Equal(First + 6, chain.TipHeight);
            Assert.Equal(fork.Header(First + 4).Hash, chain.At(First + 4)!.Hash);
            Assert.Equal(_btc.Header(First + 2).Hash, chain.At(First + 2)!.Hash);
            Assert.Null(chain.HeightOf(_btc.Header(First + 5).Hash)); // the replaced headers are gone
            Assert.Equal(First + 6, chain.HeightOf(fork.Header(First + 6).Hash));
            Assert.Equal(fork.HeaderHexes(First + 3, First + 6), chain.Hexes(First + 3, First + 6));
            Assert.Empty(chain.Hexes(First + 3, First + 7)); // not all held: nothing

            var locator = chain.Locator();
            Assert.Equal(chain.Tip.Hash, locator[0]);
            Assert.Equal(_btc.Header(Checkpoint).Hash, locator[^1]);
        }

        [Fact]
        public void TheHeaderChain_RefusesWhatIsNotBitcoin()
        {
            var chain = NewChain();
            _btc.MineEmpty(4);
            Assert.Equal(BtcHeaderAdd.Extended, chain.Add(Headers(_btc, First, First + 3), out _));

            // Another chain altogether.
            var other = new Btc(First, time: 1_770_000_000);
            other.MineEmpty(3);
            Assert.Equal(BtcHeaderAdd.Unconnected, chain.Add(Headers(other, First, First + 2), out var why));
            Assert.Contains("does not connect", why);

            // A gap: the run skips a header.
            _btc.MineEmpty(3);
            Assert.Equal(BtcHeaderAdd.Unconnected, chain.Add(Headers(_btc, First + 5, First + 6), out _));

            // A header that claims an easier target than the rules require.
            var real = _btc.Header(First + 4);
            Assert.True(BtcHeader.TryParse(real.Hex.Substring(0, 144) + "ffff7f21" + real.Hex.Substring(152), out var easier));
            Assert.Equal(BtcHeaderAdd.Invalid, chain.Add(new List<BtcHeader> { easier! }, out why));
            Assert.Contains("the rules require", why);
            Assert.Equal(First + 3, chain.TipHeight);

            // A branch that leaves the chain below the checkpoint is not followed, however long.
            var deep = _btc.ForkAfter(Checkpoint - 1);
            deep.MineEmpty(30);
            Assert.Equal(BtcHeaderAdd.Unconnected, chain.Add(Headers(deep, Checkpoint, Checkpoint + 20), out why));
            Assert.Contains("below the checkpoint", why);
            Assert.Equal(_btc.Header(Checkpoint).Hash, chain.At(Checkpoint)!.Hash);
        }

        [Fact]
        public void TheHeaderChain_FollowsRealMainnet_FromARealCheckpoint()
        {
            var known = new Dictionary<long, BtcHeader>();
            foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "btc-headers-mainnet.txt")))
            {
                var parts = line.Split(',');
                if (parts.Length == 2 && BtcHeader.TryParse(parts[1].Trim(), out var h)) known[long.Parse(parts[0])] = h!;
            }
            const long checkpointHeight = 967_680;
            var last = known.Keys.Max();
            var hex = string.Concat(Enumerable.Range(0, 12).Select(i => known[checkpointHeight - 11 + i].Hex));
            Assert.True(BtcCheckpoint.TryParse(checkpointHeight, hex, out var checkpoint, out var why), why);
            var chain = new BtcHeaderChain(checkpoint!, BtcChainRules.Mainnet);

            // As a peer serves them: at most 2,000 at a time. The range crosses a difficulty change.
            for (var from = checkpointHeight + 1; from <= last; from += 2000)
            {
                var run = Enumerable.Range(0, (int)Math.Min(2000, last - from + 1)).Select(i => known[from + i]).ToList();
                Assert.Equal(BtcHeaderAdd.Extended, chain.Add(run, out why));
            }
            Assert.Equal(last, chain.TipHeight);
            Assert.Equal(known[last].Hash, chain.Tip.Hash);

            // Regtest headers offered on top of real Bitcoin.
            Assert.Equal(BtcHeaderAdd.Unconnected, chain.Add(Headers(_btc, Checkpoint - 5, Checkpoint), out _));
        }

        /// <summary>
        /// A validator does not follow headers timed more than two hours ahead of its own clock (Bitcoin's rule; consensus
        /// cannot apply it, a node following Bitcoin can). What it does not follow it does not report or sign.
        /// </summary>
        [Fact]
        public void TheHeaderChain_DoesNotFollowHeadersTimedMoreThanTwoHoursAhead()
        {
            var prior = BtcHeaderChain.Now;
            try
            {
                var chain = NewChain();
                _btc.MineEmpty(3);
                var newest = _btc.Header(First + 2).Time;
                // The node's clock is far behind all three: nothing is followed, and the peer is not at fault for it.
                BtcHeaderChain.Now = () => newest - 3 * 3600;
                Assert.Equal(BtcHeaderAdd.Nothing, chain.Add(Headers(_btc, First, First + 2), out var why));
                Assert.Contains("two hours ahead", why);
                Assert.Equal(Checkpoint, chain.TipHeight);
                // Behind only the third (blocks are ten minutes apart): the two before it are taken.
                BtcHeaderChain.Now = () => newest - BtcHeaderChain.MaxFutureSeconds - 60;
                Assert.Equal(BtcHeaderAdd.Extended, chain.Add(Headers(_btc, First, First + 2), out _));
                Assert.Equal(First + 1, chain.TipHeight);
                // Once the clock has caught up it follows them.
                BtcHeaderChain.Now = () => newest - BtcHeaderChain.MaxFutureSeconds;
                Assert.Equal(BtcHeaderAdd.Extended, chain.Add(Headers(_btc, First, First + 2), out _));
                Assert.Equal(First + 2, chain.TipHeight);
            }
            finally { BtcHeaderChain.Now = prior; }
        }

        // ── A fetched block ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void AFetchedBlock_MustBeTheBlockOfItsHeader()
        {
            var vault = ScriptOf(NewTaprootAddress());
            var at = _btc.Mine(Pay(vault, 1_000), Pay(vault, 2_000));
            var other = _btc.Mine(Pay(vault, 3_000));
            var header = _btc.Header(at);
            BtcBlock Copy(long height) => BtcBlock.Load(_btc.Block(height).ToBytes(), Network.Main);

            Assert.Null(BtcBlockCheck.Verify(Copy(at), header));
            Assert.Equal("no block", BtcBlockCheck.Verify(null, header));
            Assert.Contains("does not carry the header", BtcBlockCheck.Verify(Copy(other), header));

            // A transaction swapped for another: the header no longer commits to the content.
            var swapped = Copy(at);
            swapped.Transactions[1] = Pay(vault, 9_999_999);
            Assert.Contains("Merkle root", BtcBlockCheck.Verify(swapped, header));

            // Bitcoin's tree pairs the last transaction of an odd row with itself, so repeating it keeps the root.
            var repeated = Copy(at);
            repeated.Transactions.Add(repeated.Transactions[2]);
            Assert.Equal(BtcMerkle.RootOf(repeated.Transactions.Select(t => t.GetHash().ToString()).ToList()), header.MerkleRoot);
            Assert.Contains("repeats a transaction", BtcBlockCheck.Verify(repeated, header));

            var noCoinbase = Copy(at);
            noCoinbase.Transactions.RemoveAt(0);
            Assert.Contains("not a coinbase", BtcBlockCheck.Verify(noCoinbase, header));
        }

        // ── The light client, against sources the test controls ─────────────────────────────────────────────

        private sealed class Source : IBtcSource
        {
            private readonly Func<Btc> _chain;
            public string Name { get; }
            public bool IsAlive { get; set; } = true;
            public Func<long, BtcBlock, BtcBlock?>? Tamper;
            public int BlockRequests;
            public int HeaderRequests;
            public bool Silent;
            public bool NoBlocks;

            public Source(string name, Func<Btc> chain)
            {
                Name = name + " test";
                _chain = chain;
            }

            public Task<List<BtcHeader>?> GetHeadersAsync(IReadOnlyList<string> locator, CancellationToken ct)
            {
                HeaderRequests++;
                if (Silent) return Task.FromResult<List<BtcHeader>?>(null);
                var chain = _chain();
                foreach (var hash in locator)
                    for (var h = chain.Tip; h >= Checkpoint - 11; h--)
                        if (chain.Header(h).Hash == hash)
                            return Task.FromResult<List<BtcHeader>?>(Enumerable.Range(1, (int)Math.Min(2000, chain.Tip - h)).Select(i => chain.Header(h + i)).ToList());
                // Knows none of them: answers from its own start, as a Bitcoin node does.
                return Task.FromResult<List<BtcHeader>?>(Enumerable.Range(0, 5).Select(i => chain.Header(Checkpoint - 11 + i)).ToList());
            }

            public Task<BtcBlock?> GetBlockAsync(string hash, CancellationToken ct)
            {
                BlockRequests++;
                if (Silent || NoBlocks) return Task.FromResult<BtcBlock?>(null);
                var chain = _chain();
                for (var h = chain.Tip; h >= Checkpoint - 11; h--)
                    if (chain.Header(h).Hash == hash)
                    {
                        var block = BtcBlock.Load(chain.Block(h).ToBytes(), Network.Main);
                        return Task.FromResult(Tamper == null ? block : Tamper(h, block));
                    }
                return Task.FromResult<BtcBlock?>(null);
            }

            public void Dispose() { IsAlive = false; }
        }

        /// <summary>
        /// A peer that answers headers but never delivers blocks was never dropped: each wanted block waited on it a
        /// minute before anyone else was asked, on every pass (fifth review, round 22). Two misses running drop it.
        /// </summary>
        [Fact]
        public async Task APeerThatNeverDeliversBlocks_IsDroppedAfterTwoMisses()
        {
            await Start();
            var vault = ScriptOf(NewTaprootAddress());
            var first = _btc.Mine(Pay(vault, 5_000));
            var second = _btc.Mine(Pay(vault, 6_000));
            _btc.MineEmpty(2);
            var mute = new Source("10.9.0.1:8333", () => _btc) { NoBlocks = true };
            var honest = new Source("explorer", () => _btc);
            using var client = NewClient(new[] { mute }, new List<IBtcSource> { honest });
            await client.RunOnceAsync(CancellationToken.None);                       // headers
            Assert.Null(client.BlockAt(first));                                       // wanted
            Assert.Null(client.BlockAt(second));
            await client.RunOnceAsync(CancellationToken.None);
            Assert.NotNull(client.BlockAt(first));                                    // from the explorer behind it
            Assert.False(mute.IsAlive);
            Assert.Contains("delivered no block", client.LastProblem ?? "");
        }

        private BtcLightClient NewClient(IReadOnlyList<Source> peers, List<IBtcSource>? fallbacks = null, Func<List<IBtcSource>>? validators = null)
        {
            var endpoints = peers.Select((_, i) => new IPEndPoint(IPAddress.Parse($"10.9.0.{i + 1}"), 8333)).ToList();
            return new BtcLightClient(Network.Main, NewChain(), _blockDir,
                connect: (_, endpoint) => peers[endpoints.FindIndex(e => e.Equals(endpoint))],
                discover: _ => Task.FromResult(endpoints.ToList()),
                fallbacks: fallbacks ?? new List<IBtcSource>(),
                validators: validators);
        }

        // ── Other validators as a source of headers ─────────────────────────────────────────────────────────

        /// <summary>
        /// A validator whose every Bitcoin peer is in one party's hands sees the chain that party shows it. Shown to
        /// two thirds of the signers, that chain is the one reported. Validators therefore ask each other for the
        /// headers they follow; headers prove their own work, and the branch with the most is followed.
        /// </summary>
        [Fact]
        public async Task AValidatorCutOffFromBitcoin_LearnsTheHeaviestChain_FromAnotherValidator()
        {
            _btc.MineEmpty(2);
            // What the validator's only peer shows it: a branch of its own making, shorter than Bitcoin's.
            var shown = _btc.ForkAfter(_btc.Tip);
            var fake = shown.Mine(Pay(ScriptOf(NewTaprootAddress()), 900_000_000));
            shown.MineEmpty(3);
            _btc.MineEmpty(6); // Bitcoin went on meanwhile
            var onlyPeer = new Source("10.9.0.1:8333", () => shown);

            // Alone with that peer, the validator follows what it is shown.
            using (var alone = NewClient(new[] { onlyPeer }))
            {
                await alone.RunOnceAsync(CancellationToken.None);
                Assert.Equal(shown.Header(fake).Hash, alone.HeaderAt(fake)!.Hash);
            }

            // Asking one other validator is enough: the heavier chain is followed, and the block made for it is on
            // no chain this validator offers its signature for.
            var other = new Source("validator-b", () => _btc);
            using var client = NewClient(new[] { new Source("10.9.0.1:8333", () => shown) }, validators: () => new List<IBtcSource> { other });
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Equal(_btc.Tip, client.TipHeight);
            Assert.Equal(_btc.Header(fake).Hash, client.HeaderAt(fake)!.Hash);
            Assert.NotEqual(shown.Header(fake).Hash, client.HeaderAt(fake)!.Hash);
            Assert.Equal(1, client.PeerCount); // the peer is kept: a weaker branch is not an offence
            // The validator serves no blocks, and the peer does not have this one: it is waited for, not guessed.
            Assert.Null(client.BlockAt(fake));
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Null(client.BlockAt(fake));
            Assert.Equal(0, other.BlockRequests);
        }

        [Fact]
        public async Task AValidatorServingHeadersThatAreNotBitcoin_ChangesNothing_AndIsLeftAloneForAWhile()
        {
            _btc.MineEmpty(4);
            var elsewhere = new Btc(Checkpoint - 11, time: 1_770_000_000);
            elsewhere.MineEmpty(40);
            var liar = new Source("validator-liar", () => elsewhere);
            // A branch with less work than Bitcoin's, from a validator: not followed, and no offence.
            var weak = _btc.ForkAfter(_btc.Tip - 3);
            weak.MineEmpty(1);
            var behind = new Source("validator-behind", () => weak);
            var peer = new Source("10.9.0.1:8333", () => _btc);
            using var client = NewClient(new[] { peer }, validators: () => new List<IBtcSource> { liar, behind });
            client.ValidatorRound = TimeSpan.Zero;

            await client.RunOnceAsync(CancellationToken.None);
            Assert.Equal(_btc.Tip, client.TipHeight);
            Assert.Equal(_btc.Tip.ToString(), client.Chain.HeightOf(_btc.Header(_btc.Tip).Hash)!.Value.ToString());
            Assert.Equal(1, client.PeerCount);
            Assert.Equal(1, liar.HeaderRequests);

            await client.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, liar.HeaderRequests);          // not asked again this hour
            Assert.True(behind.HeaderRequests >= 2);       // being behind is not a reason to stop asking
            Assert.Equal(_btc.Tip, client.TipHeight);
        }

        /// <summary>A stream that never ends and says nothing of its length, as a validator streaming forever would send.</summary>
        private sealed class EndlessStream : Stream
        {
            public long Served;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => Served; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'0', offset, count); Served += count; return count; }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// A validator's answer was read whole before its size was looked at, and the client allowed 2 GB: one validator
        /// streaming for the eight seconds it is given filled the asking validator's memory. Only a full answer's worth is
        /// read now (third pass review, 14 October 2026).
        /// </summary>
        [Fact]
        public async Task AValidatorsAnswer_IsReadOnlyAsFarAsAFullAnswerGoes()
        {
            var max = BtcValidatorSource.MaxAnswerBytes;
            Assert.Equal("ok", await BtcValidatorSource.ReadBoundedAsync(new System.Net.Http.StringContent("ok"), max, CancellationToken.None));
            Assert.NotNull(await BtcValidatorSource.ReadBoundedAsync(new System.Net.Http.StringContent(new string('0', max)), max, CancellationToken.None));
            Assert.Null(await BtcValidatorSource.ReadBoundedAsync(new System.Net.Http.StringContent(new string('0', max + 1)), max, CancellationToken.None));

            var endless = new EndlessStream();
            Assert.Null(await BtcValidatorSource.ReadBoundedAsync(new System.Net.Http.StreamContent(endless), max, CancellationToken.None));
            Assert.InRange(endless.Served, max + 1, max + 1 + 81920);
        }

        [Fact]
        public void WhatAValidatorServes_IsItsBestChainAfterTheNewestHashItIsGiven()
        {
            _btc.MineEmpty(10);
            var chain = NewChain();
            Assert.Equal(BtcHeaderAdd.Extended, chain.Add(Headers(_btc, First, _btc.Tip), out _));

            Assert.Equal(_btc.HeaderHexes(First + 3, _btc.Tip), chain.HexesAfter(new[] { "00", _btc.Header(First + 2).Hash, _btc.Header(First).Hash }, 500));
            Assert.Equal(_btc.HeaderHexes(First + 1, First + 4), chain.HexesAfter(new[] { _btc.Header(First).Hash }, 4));
            Assert.Empty(chain.HexesAfter(new[] { _btc.Header(_btc.Tip).Hash }, 500));
            Assert.Empty(chain.HexesAfter(new[] { new string('0', 64) }, 500));
            Assert.Empty(chain.HexesAfter(null, 500));
        }

        [Fact]
        public async Task Peers_AreTakenFromDifferentNetworks_WhileThereAreSuchToTake()
        {
            Assert.Equal("10.9.0.0/16", BtcLightClient.NetworkGroup("10.9.0.7:8333 /Satoshi:27.0.0/"));
            Assert.Equal("10.9.0.0/16", BtcLightClient.NetworkGroup("10.9.200.1:8333"));
            Assert.NotEqual(BtcLightClient.NetworkGroup("10.9.0.7:8333"), BtcLightClient.NetworkGroup("10.10.0.7:8333"));

            _btc.MineEmpty(1);
            // Thirty addresses in one provider's network, and seven elsewhere.
            var endpoints = Enumerable.Range(1, 30).Select(i => new IPEndPoint(IPAddress.Parse($"10.9.0.{i}"), 8333))
                .Concat(Enumerable.Range(1, 7).Select(i => new IPEndPoint(IPAddress.Parse($"10.{20 + i}.0.1"), 8333))).ToList();
            var connected = new List<string>();
            using var client = new BtcLightClient(Network.Main, NewChain(), _blockDir,
                connect: (_, endpoint) => { lock (connected) connected.Add(endpoint.ToString()); return new Source(endpoint.ToString(), () => _btc); },
                discover: _ => Task.FromResult(endpoints.ToList()),
                fallbacks: new List<IBtcSource>());
            await client.RunOnceAsync(CancellationToken.None);

            Assert.Equal(BtcLightClient.TargetPeers, client.PeerCount);
            // All seven of the other networks, and one peer of the large one.
            Assert.Equal(8, connected.Select(BtcLightClient.NetworkGroup).Distinct().Count());
        }

        [Fact]
        public async Task TheLightClient_SyncsHeaders_FetchesBlocksOnRequest_AndKeepsThemOnDisk()
        {
            var vault = ScriptOf(NewTaprootAddress());
            _btc.MineEmpty(3);
            var at = _btc.Mine(Pay(vault, 5_000));
            _btc.MineEmpty(2);
            var peer = new Source("10.9.0.1:8333", () => _btc);
            using var client = NewClient(new[] { peer });

            await client.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, client.PeerCount);
            Assert.Equal(_btc.Tip, client.TipHeight);
            Assert.Equal(_btc.Header(at).Hash, client.HeaderAt(at)!.Hash);
            Assert.Equal(0, peer.BlockRequests); // headers only until a block is asked for

            Assert.Null(client.BlockAt(at)); // not fetched: queued
            await client.RunOnceAsync(CancellationToken.None);
            var block = client.BlockAt(at);
            Assert.NotNull(block);
            Assert.Null(BtcBlockCheck.Verify(block, _btc.Header(at)));
            Assert.Equal(1, peer.BlockRequests);
            Assert.Single(Directory.GetFiles(_blockDir, "*.blk"));

            // A new process finds the block on disk and asks nobody.
            var idle = new Source("10.9.0.1:8333", () => _btc);
            using var restarted = NewClient(new[] { idle });
            await restarted.RunOnceAsync(CancellationToken.None);
            Assert.NotNull(restarted.BlockAt(at));
            Assert.Equal(0, idle.BlockRequests);

            // A file damaged on disk is not believed: it is fetched again.
            var file = Directory.GetFiles(_blockDir, "*.blk").Single();
            var bytes = File.ReadAllBytes(file);
            bytes[^5] ^= 0x01;
            File.WriteAllBytes(file, bytes);
            using var third = NewClient(new[] { idle });
            await third.RunOnceAsync(CancellationToken.None);
            Assert.Null(third.BlockAt(at));
            await third.RunOnceAsync(CancellationToken.None);
            Assert.NotNull(third.BlockAt(at));
            Assert.Equal(1, idle.BlockRequests);
        }

        [Fact]
        public async Task APeerThatLies_IsDropped_AndTheBlockComesFromAnother()
        {
            var vault = ScriptOf(NewTaprootAddress());
            var at = _btc.Mine(Pay(vault, 5_000), Pay(vault, 6_000));
            _btc.MineEmpty(2);
            var liar = new Source("10.9.0.1:8333", () => _btc)
            {
                // Leaves a deposit out, and repeats another so that the transaction count still looks right.
                Tamper = (_, block) => { block.Transactions[1] = block.Transactions[2]; return block; },
            };
            // The liar is the only peer, so it is the one asked; the honest source stands behind it. (Peers are tried
            // in the order they connected, which is shuffled, so two peers would make "who was asked" a coin toss.)
            var honest = new Source("explorer", () => _btc);
            using var client = NewClient(new[] { liar }, new List<IBtcSource> { honest });
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, client.PeerCount);

            Assert.Null(client.BlockAt(at));
            await client.RunOnceAsync(CancellationToken.None);
            var block = client.BlockAt(at);
            Assert.NotNull(block);
            Assert.Equal(3, block!.Transactions.Count);
            Assert.Null(BtcBlockCheck.Verify(block, _btc.Header(at)));
            Assert.Equal(1, liar.BlockRequests);
            Assert.False(liar.IsAlive);
            Assert.Contains("served a wrong block", client.LastProblem);
            Assert.Equal(1, honest.BlockRequests);

            // With two peers, one of them lying, the block that is kept is the right one whichever is asked first.
            Directory.Delete(_blockDir, recursive: true);
            var liar2 = new Source("10.9.0.1:8333", () => _btc) { Tamper = liar.Tamper };
            var peer2 = new Source("10.9.0.2:8333", () => _btc);
            using var both = NewClient(new[] { liar2, peer2 });
            await both.RunOnceAsync(CancellationToken.None);
            Assert.Null(both.BlockAt(at));
            await both.RunOnceAsync(CancellationToken.None);
            Assert.Null(BtcBlockCheck.Verify(both.BlockAt(at), _btc.Header(at)));
            Assert.Equal(liar2.BlockRequests == 1, !liar2.IsAlive); // asked means caught
        }

        [Fact]
        public async Task APeerOnAnotherChain_IsDropped_AndAFallbackServesWhenNoPeerDoes()
        {
            var elsewhere = new Btc(Checkpoint - 11, time: 1_770_000_000);
            elsewhere.MineEmpty(40);
            _btc.MineEmpty(4);
            var stranger = new Source("10.9.0.1:8333", () => elsewhere);
            var explorer = new Source("explorer", () => _btc);
            using var client = NewClient(new[] { stranger }, new List<IBtcSource> { explorer });

            await client.RunOnceAsync(CancellationToken.None);
            Assert.False(stranger.IsAlive); // its headers do not connect to the checkpoint
            Assert.Equal(0, client.PeerCount);
            Assert.Equal(Checkpoint, client.TipHeight);

            // With no peer left, the fallback keeps the header chain moving, and serves blocks.
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Equal(_btc.Tip, client.TipHeight);
            Assert.Null(client.BlockAt(First));
            await client.RunOnceAsync(CancellationToken.None);
            Assert.NotNull(client.BlockAt(First));
        }

        [Fact]
        public async Task WhenBitcoinReorganises_TheViewFollows_AndTheLosingBlocksAreRemoved()
        {
            var vault = ScriptOf(NewTaprootAddress());
            _btc.MineEmpty(2);
            var current = _btc;
            var peer = new Source("10.9.0.1:8333", () => current);
            using var client = NewClient(new[] { peer });
            var losing = _btc.Mine(Pay(vault, 5_000));
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Null(client.BlockAt(losing));
            await client.RunOnceAsync(CancellationToken.None);
            Assert.NotNull(client.BlockAt(losing));
            var losingHash = _btc.Header(losing).Hash;

            var fork = _btc.ForkAfter(losing - 1);
            fork.MineEmpty(2);
            current = fork;
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Equal(fork.Tip, client.TipHeight);
            Assert.Equal(fork.Header(losing).Hash, client.HeaderAt(losing)!.Hash);
            Assert.DoesNotContain(Directory.GetFiles(_blockDir, "*.blk"), f => f.Contains(losingHash));

            Assert.Null(client.BlockAt(losing)); // the block now at that height is another block
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Single(client.BlockAt(losing)!.Transactions); // the fork's block: a coinbase only
        }

        /// <summary>
        /// The real thing, when asked for (set VFX_BTC_LIVE=1; it needs the Internet): real peers found through the DNS
        /// seeds, on mainnet and on testnet4, from the real checkpoints in the fixtures. The header chain must pass the
        /// fixture's last header and agree with it, and a recent block must arrive and check out against its header.
        /// </summary>
        [Fact]
        public async Task Live_TheLightClient_FollowsRealBitcoin_OnBothNetworks()
        {
            if (Environment.GetEnvironmentVariable("VFX_BTC_LIVE") != "1") return;

            foreach (var (network, rules, fixture, checkpointHeight) in new[]
            {
                (Network.Main, BtcChainRules.Mainnet, "btc-headers-mainnet.txt", 967_680L),
                (Network.TestNet4, BtcChainRules.Testnet4, "btc-headers-testnet4.txt", 153_216L),
            })
            {
                var known = new Dictionary<long, BtcHeader>();
                foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)))
                {
                    var parts = line.Split(',');
                    if (parts.Length == 2 && BtcHeader.TryParse(parts[1].Trim(), out var h)) known[long.Parse(parts[0])] = h!;
                }
                var top = known.Keys.Max();
                var hex = string.Concat(Enumerable.Range(0, 12).Select(i => known[checkpointHeight - 11 + i].Hex));
                Assert.True(BtcCheckpoint.TryParse(checkpointHeight, hex, out var checkpoint, out var why), why);
                var dir = Path.Combine(_blockDir, network.Name);
                using var client = new BtcLightClient(network, new BtcHeaderChain(checkpoint!, rules), dir);

                for (var i = 0; i < 8 && client.TipHeight <= top; i++)
                    await client.RunOnceAsync(CancellationToken.None);
                Assert.True(client.PeerCount >= 1, $"{network.Name}: no Bitcoin peer ({client.LastProblem})");
                Assert.True(client.TipHeight > top, $"{network.Name}: headers stopped at {client.TipHeight} ({client.LastProblem})");
                Assert.Equal(known[top].Hash, client.HeaderAt(top)!.Hash);

                var recent = client.TipHeight - 3;
                Assert.Null(client.BlockAt(recent));
                for (var i = 0; i < 4 && client.BlockAt(recent) == null; i++)
                    await client.RunOnceAsync(CancellationToken.None);
                var block = client.BlockAt(recent);
                Assert.True(block != null, $"{network.Name}: block {recent} did not arrive ({client.LastProblem})");
                Assert.Null(BtcBlockCheck.Verify(block, client.HeaderAt(recent)!));
                Assert.DoesNotContain(block!.Transactions, t => t.HasWitness); // asked for without witness data
            }
        }

        /// <summary>
        /// The checkpoints this build ships with (Globals), against the real networks (set VFX_BTC_LIVE=1): from each,
        /// the light client must follow real peers' headers under the real rules to a tip well past the checkpoint,
        /// and fetch and check a recent block. This is what a validator does from the observation height on.
        /// </summary>
        [Fact]
        public async Task Live_TheConfiguredCheckpoints_LeadToTodaysBitcoin_OnBothNetworks()
        {
            if (Environment.GetEnvironmentVariable("VFX_BTC_LIVE") != "1") return;

            foreach (var (network, rules, height, hex) in new[]
            {
                (Network.Main, BtcChainRules.Mainnet, Globals.BitcoinCheckpointHeightMainnet, Globals.BitcoinCheckpointHeadersMainnet),
                (Network.TestNet4, BtcChainRules.Testnet4, Globals.BitcoinCheckpointHeightTestnet, Globals.BitcoinCheckpointHeadersTestnet),
            })
            {
                Assert.True(BtcCheckpoint.TryParse(height, hex, out var checkpoint, out var why), why);
                var dir = Path.Combine(_blockDir, "configured-" + network.Name);
                using var client = new BtcLightClient(network, new BtcHeaderChain(checkpoint!, rules), dir);
                var before = -1L;
                for (var i = 0; i < 12 && client.TipHeight != before; i++)
                {
                    before = client.TipHeight;
                    await client.RunOnceAsync(CancellationToken.None);
                }
                Assert.True(client.PeerCount >= 1, $"{network.Name}: no Bitcoin peer ({client.LastProblem})");
                Assert.True(client.TipHeight > height + 500, $"{network.Name}: headers stopped at {client.TipHeight}, checkpoint {height} ({client.LastProblem})");
                Assert.Equal(checkpoint!.Tip.Hash, client.HeaderAt(height)!.Hash);

                var recent = client.TipHeight - 3;
                for (var i = 0; i < 4 && client.BlockAt(recent) == null; i++)
                    await client.RunOnceAsync(CancellationToken.None);
                var block = client.BlockAt(recent);
                Assert.True(block != null, $"{network.Name}: block {recent} did not arrive ({client.LastProblem})");
                Assert.Null(BtcBlockCheck.Verify(block, client.HeaderAt(recent)!));
                Assert.True(client.TipHeight > height, $"{network.Name}: checkpoint {height}, followed to {client.TipHeight} from {client.PeerCount} peer(s)");
            }
        }

        [Fact]
        public async Task OldBlocksAreRemoved_PastWhatReportBuildingCanAskFor()
        {
            var vault = ScriptOf(NewTaprootAddress());
            var early = _btc.Mine(Pay(vault, 5_000));
            var peer = new Source("10.9.0.1:8333", () => _btc);
            using var client = NewClient(new[] { peer });
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Null(client.BlockAt(early));
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Single(Directory.GetFiles(_blockDir, "*.blk"));

            _btc.MineEmpty(BtcLightClient.KeepBlocks);
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Single(Directory.GetFiles(_blockDir, "*.blk")); // exactly at the edge: kept
            _btc.MineEmpty(1);
            await client.RunOnceAsync(CancellationToken.None);
            Assert.Empty(Directory.GetFiles(_blockDir, "*.blk"));
        }
    }
}
