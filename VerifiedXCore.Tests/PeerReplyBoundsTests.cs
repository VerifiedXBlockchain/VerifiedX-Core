using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// What another node's answer can cost the node that asked: a short deadline for the whole exchange, the status
    /// looked at before the body, and no more of the body read than a short answer holds. The collectors of mint
    /// signatures ask their signers at once and stop when they have enough, so a signer that stays silent or answers
    /// with something long does not hold back the others.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PeerReplyBoundsTests : IDisposable
    {
        private const long NinetySixMiB = 96L * 1024 * 1024;
        private readonly HttpMessageHandler? _priorHandler = NodeHttp.PeerHandlerForTests;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"peer_reply_bounds_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;

        public PeerReplyBoundsTests()
        {
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot; // the collectors log
        }

        public void Dispose()
        {
            NodeHttp.PeerHandlerForTests = _priorHandler;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>Answers requests in place of the network, by the host asked.</summary>
        private sealed class Peers : HttpMessageHandler
        {
            private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _answer;
            public int Requests;
            public Peers(Func<string, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            {
                Interlocked.Increment(ref Requests);
                return _answer(request.RequestUri!.Host, cancel);
            }
        }

        /// <summary>A body of <paramref name="length"/> bytes made as it is read, counting what was read; or one that stalls after its headers.</summary>
        private sealed class Body : Stream
        {
            private readonly long _length;
            private readonly bool _stalls;
            public long Served;
            public Body(long length, bool stalls = false) { _length = length; _stalls = stalls; }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
            {
                if (_stalls) await Task.Delay(Timeout.Infinite, cancel);
                var n = (int)Math.Min(buffer.Length, _length - Served);
                buffer.Span[..n].Fill((byte)'a');
                Served += n;
                return n;
            }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => Served; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, HttpContent content) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content });

        private static async Task<HttpResponseMessage> Silent(CancellationToken cancel)
        {
            await Task.Delay(Timeout.Infinite, cancel);
            throw new InvalidOperationException();
        }

        [Fact]
        public async Task AShortSuccessfulAnswer_IsReturned()
        {
            NodeHttp.PeerHandlerForTests = new Peers((_, _) => Answer(HttpStatusCode.OK, new StringContent("{\"success\":true}")));
            Assert.Equal("{\"success\":true}", await NodeHttp.PostForShortReplyAsync("http://10.0.0.1:1/x", "{}"));

            var atTheLimit = new string('a', NodeHttp.MaxPeerReplyBytes);
            NodeHttp.PeerHandlerForTests = new Peers((_, _) => Answer(HttpStatusCode.OK, new StringContent(atTheLimit)));
            Assert.Equal(atTheLimit, await NodeHttp.PostForShortReplyAsync("http://10.0.0.1:1/x", "{}"));
        }

        [Fact]
        public async Task ALongAnswer_IsNotTaken_AndOnlyItsStartIsRead()
        {
            var body = new Body(NinetySixMiB);
            NodeHttp.PeerHandlerForTests = new Peers((_, _) => Answer(HttpStatusCode.OK, new StreamContent(body)));

            Assert.Null(await NodeHttp.PostForShortReplyAsync("http://10.0.0.1:1/x", "{}"));
            Assert.InRange(body.Served, NodeHttp.MaxPeerReplyBytes + 1, 4 * NodeHttp.MaxPeerReplyBytes);

            // One byte over the limit is already too long.
            NodeHttp.PeerHandlerForTests = new Peers((_, _) => Answer(HttpStatusCode.OK, new StringContent(new string('a', NodeHttp.MaxPeerReplyBytes + 1))));
            Assert.Null(await NodeHttp.PostForShortReplyAsync("http://10.0.0.1:1/x", "{}"));
        }

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.Redirect)]
        public async Task AnAnswerWithAnotherStatus_IsNotTaken_AndNoneOfItsBodyIsRead(HttpStatusCode status)
        {
            var body = new Body(NinetySixMiB);
            NodeHttp.PeerHandlerForTests = new Peers((_, _) => Answer(status, new StreamContent(body)));

            Assert.Null(await NodeHttp.PostForShortReplyAsync("http://10.0.0.1:1/x", "{}"));
            Assert.Equal(0, body.Served);
        }

        [Fact]
        public async Task ANodeThatDoesNotAnswer_OrStallsAfterItsHeaders_EndsTheWaitAtTheDeadline()
        {
            var deadline = TimeSpan.FromMilliseconds(300);
            NodeHttp.PeerHandlerForTests = new Peers((_, cancel) => Silent(cancel));
            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodeHttp.PostForShortReplyAsync("http://10.0.0.1:1/x", "{}", timeout: deadline));

            NodeHttp.PeerHandlerForTests = new Peers((_, _) => Answer(HttpStatusCode.OK, new StreamContent(new Body(100, stalls: true))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodeHttp.PostForShortReplyAsync("http://10.0.0.1:1/x", "{}", timeout: deadline));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"waited {watch.Elapsed.TotalSeconds:F1} s");
        }

        [Fact]
        public async Task AnAnswerThatIsNotUsed_IsNotRead()
        {
            var body = new Body(NinetySixMiB);
            NodeHttp.PeerHandlerForTests = new Peers((_, _) => Answer(HttpStatusCode.OK, new StreamContent(body)));

            await NodeHttp.PostWithoutReadingAsync("http://10.0.0.1:1/x", "{}");
            Assert.Equal(0, body.Served);
        }

        // ── The collectors of mint signatures ───────────────────────────────────────────────────────────────

        private static (byte[] Key, UserBridgeMintService.ContractSigner Signer) NewSigner(int n)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            return (key, new UserBridgeMintService.ContractSigner(ValidatorEthKeyService.DeriveBaseAddress(key), $"10.1.0.{n}", $"xSigner{n}", true, false));
        }

        private static StringContent Signed(byte[] digest, byte[] key) =>
            new StringContent($"{{\"success\":true,\"signature\":\"{ValidatorEthKeyService.SignDigestHex(digest, key)}\"}}");

        [Fact]
        public async Task SilentAndLongAnsweringSigners_DoNotHoldBackTheOnesThatSign()
        {
            var digest = RandomNumberGenerator.GetBytes(32);
            var signers = Enumerable.Range(1, 7).Select(NewSigner).ToList();
            var long96 = new Body(NinetySixMiB);
            var refused = new Body(NinetySixMiB);
            var peers = new Peers((host, cancel) => host switch
            {
                "10.1.0.1" or "10.1.0.2" => Silent(cancel),                                     // never answer
                "10.1.0.3" => Answer(HttpStatusCode.OK, new StreamContent(long96)),              // 96 MiB that is not a signature
                "10.1.0.4" => Answer(HttpStatusCode.BadRequest, new StreamContent(refused)),     // a refusal with 96 MiB behind it
                "10.1.0.5" => Answer(HttpStatusCode.OK, Signed(digest, signers[5].Key)),         // signs with another signer's key
                _ => Answer(HttpStatusCode.OK, Signed(digest, signers.Single(s => s.Signer.IPAddress == host).Key)),
            });
            NodeHttp.PeerHandlerForTests = peers;
            var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var watch = Stopwatch.StartNew();
            await UserBridgeMintService.AskSignersAsync(signers.Select(s => s.Signer), "{}", digest, typed: true, signatures, signed => signed.Count() >= 2, "lock");
            watch.Stop();

            // The two that sign are enough, well inside the time a silent signer is given.
            Assert.Equal(new[] { signers[5].Signer.BaseAddress, signers[6].Signer.BaseAddress }.OrderBy(a => a), signatures.Keys.OrderBy(a => a));
            Assert.True(watch.Elapsed < NodeHttp.PeerReplyTimeout / 2, $"collected in {watch.Elapsed.TotalSeconds:F1} s");
            Assert.Equal(7, peers.Requests);
            Assert.True(long96.Served <= 4 * NodeHttp.MaxPeerReplyBytes, $"{long96.Served} bytes of the long answer were read");
            Assert.Equal(0, refused.Served);
        }

        [Fact]
        public async Task WithAWaitForSpareSignatures_TheSignersThatAnswerInItAreKeptToo_AndASilentOneEndsItNoLater()
        {
            var digest = RandomNumberGenerator.GetBytes(32);
            var signers = Enumerable.Range(1, 6).Select(NewSigner).ToList();
            NodeHttp.PeerHandlerForTests = new Peers(async (host, cancel) =>
            {
                if (host == "10.1.0.1") return await Silent(cancel);
                if (host is "10.1.0.5" or "10.1.0.6") await Task.Delay(300, cancel); // a little slower than the first two
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = Signed(digest, signers.Single(s => s.Signer.IPAddress == host).Key) };
            });
            var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var watch = Stopwatch.StartNew();
            await UserBridgeMintService.AskSignersAsync(signers.Select(s => s.Signer), "{}", digest, typed: true, signatures, signed => signed.Count() >= 2, "lock",
                graceAfterEnough: TimeSpan.FromSeconds(2));
            watch.Stop();

            Assert.Equal(signers.Skip(1).Select(s => s.Signer.BaseAddress).OrderBy(a => a), signatures.Keys.OrderBy(a => a));
            Assert.True(watch.Elapsed < NodeHttp.PeerReplyTimeout - TimeSpan.FromSeconds(2), $"collected in {watch.Elapsed.TotalSeconds:F1} s");
        }

        [Fact]
        public async Task WhenEverySignerIsNeeded_EveryOneIsAsked_AndThoseAlreadyHeldAreNotAskedAgain()
        {
            var digest = RandomNumberGenerator.GetBytes(32);
            var signers = Enumerable.Range(1, 5).Select(NewSigner).ToList();
            var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
            NodeHttp.PeerHandlerForTests = new Peers((host, _) =>
            {
                asked.Add(host);
                return Answer(HttpStatusCode.OK, Signed(digest, signers.Single(s => s.Signer.IPAddress == host).Key));
            });
            var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [signers[0].Signer.BaseAddress] = "held" };

            await UserBridgeMintService.AskSignersAsync(signers.Select(s => s.Signer), "{}", digest, typed: true, signatures, signed => signed.Count() >= 5, "lock");

            Assert.Equal(5, signatures.Count);
            Assert.Equal("held", signatures[signers[0].Signer.BaseAddress]);
            Assert.DoesNotContain("10.1.0.1", asked);
            Assert.Equal(4, asked.Count);
            Assert.All(signers.Skip(1), s => Assert.Equal(s.Signer.BaseAddress, ValidatorEthKeyService.RecoverSigner(digest, signatures[s.Signer.BaseAddress], personalMessage: false), StringComparer.OrdinalIgnoreCase));
        }
    }
}
