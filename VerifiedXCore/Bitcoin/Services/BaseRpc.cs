using System.Collections.Concurrent;
using System.Numerics;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>One read of Base: the answer, or why there is none (<see cref="BaseRpc"/>).</summary>
    public readonly struct BaseAnswer<T>
    {
        private BaseAnswer(bool answered, T? value, string reason)
        {
            Answered = answered;
            Value = value;
            Reason = reason;
        }

        /// <summary>Whether a provider answered the read.</summary>
        public bool Answered { get; }
        public T? Value { get; }
        /// <summary>Why there is no answer: what the last provider asked said, or that none could be asked.</summary>
        public string Reason { get; }

        public static BaseAnswer<T> Yes(T value) => new(true, value, "");
        public static BaseAnswer<T> No(string reason) => new(false, default, reason ?? "");
    }

    /// <summary>A block of Base as a provider names it: its number and its hash.</summary>
    public readonly record struct BaseBlock(long Number, string Hash);

    /// <summary>What asking a provider for its chain id came to (<see cref="BaseRpc.CheckProvidersAtStartAsync"/>).</summary>
    public enum BaseProviderCheck
    {
        /// <summary>The provider serves this network's Base chain.</summary>
        OnBase,
        /// <summary>The provider serves another chain: it is set aside, and asked again later.</summary>
        OtherChain,
        /// <summary>The provider did not answer: it is asked again at the next read.</summary>
        NoAnswer,
    }

    /// <summary>
    /// A read of Base that failed because of the provider, not because of what was asked of it: the provider is not on
    /// this network's Base chain or did not say which chain it is on, or what it served is not Base's. Asking the same
    /// again of a provider that is right would answer; nothing about the block or the transaction asked for is learnt
    /// from it.
    /// </summary>
    public sealed class BaseProviderException : InvalidOperationException
    {
        public BaseProviderException(string message) : base(message) { }
    }

    /// <summary>
    /// Every read of Base and every transaction sent to it goes through here: this is the one place a client for a Base
    /// provider is made. A read is one of two kinds, and the caller says which by the method it calls.
    ///
    /// Authorizing: the answer decides something of value (a burn that pays an exit, the contract's signers and pending
    /// changes that a signature is given on, the code an upgrade is approved for).
    /// Advisory: the answer is shown, or sizes a request of this node's own (a balance, gas, a local record's status),
    /// and the contract or the chain judges what follows.
    ///
    /// An authorizing read takes only what is final on Base: a provider is asked for its finalized block (the newest
    /// block Base will not replace), contract state is read at that one block, and a transaction's receipt counts only
    /// from a block at or below it that is still on the provider's chain. What is newer is not refused, it is not
    /// answered yet (<see cref="BaseAnswer{T}.Answered"/> false): the caller asks again later. An advisory read is of
    /// the newest block.
    ///
    /// An authorizing read is not one provider's word: every configured provider is asked, and there is an answer only
    /// when <see cref="RequiredAgreement"/> of them, at different addresses, give the same one. That holds for "no" as
    /// for "yes". With fewer in agreement there is no answer yet. The burn watch takes a burn any provider serves as
    /// one to look into (so no single provider can hide a burn), and passes a range only once
    /// <see cref="RequiredAgreement"/> providers have read it; what it found is then held to the same agreement before
    /// anything is signed for it.
    ///
    /// A provider is asked only while it serves this network's Base chain (<see cref="Globals.BaseEvmChainId"/>, by its
    /// own eth_chainId). One that serves another chain is set aside for <see cref="SetAsideSeconds"/> and then asked
    /// again; none is set aside for good.
    /// </summary>
    public static class BaseRpc
    {
        /// <summary>The reason given when no provider is configured, or none was reached.</summary>
        public const string NoProviderReason = "no RPC reachable";

        /// <summary>Why a receipt is not taken yet: no provider has the transaction.</summary>
        public const string ReceiptNotFound = "receipt not found";

        /// <summary>Why a receipt is not taken yet: its block is newer than the provider's finalized block.</summary>
        public const string ReceiptNotFinal = "the transaction's block is not final on Base yet";

        /// <summary>Why a receipt is not taken: the block it names is not the one the provider's chain holds at that height.</summary>
        public const string ReceiptOffChain = "the transaction's block is not on Base's chain";

        /// <summary>Why a provider gives no authorizing answer: it names no finalized block.</summary>
        public const string NoFinalizedBlock = "the provider gave no finalized block";

        /// <summary>How many providers must give the same answer before an authorizing read has one.</summary>
        public const int RequiredAgreement = 2;

        /// <summary>Why an authorizing read has no answer yet although a provider gave one: too few gave the same.</summary>
        public const string NoAgreement = "fewer than 2 Base providers gave the same answer";

        /// <summary>Seconds a provider found on Base is used before its chain id is asked again.</summary>
        internal const int VerifiedForSeconds = 600;

        /// <summary>Seconds a provider found on another chain is left out before its chain id is asked again.</summary>
        internal const int SetAsideSeconds = 60;

        /// <summary>The clock the chain-id checks are timed by (seconds).</summary>
        internal static Func<long> Clock = () => TimeUtil.GetTime();

        private sealed record ChainCheck(long At, bool OnBase, string Found);

        private static readonly ConcurrentDictionary<string, ChainCheck> ChainChecks = new(StringComparer.Ordinal);

        /// <summary>
        /// The providers of Base mainnet a node asks where its configuration names none: three public endpoints run by
        /// three different parties, since what decides something of value needs <see cref="RequiredAgreement"/> providers
        /// that give the same answer. An operator's own providers, set in config.txt, take their place.
        /// </summary>
        public static readonly IReadOnlyList<string> MainnetDefaultProviders = new[]
        {
            "https://mainnet.base.org", "https://base-rpc.publicnode.com", "https://base.drpc.org",
        };

        /// <summary>The same for Base Sepolia, the testnets' Base chain.</summary>
        public static readonly IReadOnlyList<string> TestnetDefaultProviders = new[]
        {
            "https://sepolia.base.org", "https://base-sepolia-rpc.publicnode.com", "https://base-sepolia.drpc.org",
        };

        /// <summary>The default providers of this network's Base chain.</summary>
        public static IReadOnlyList<string> DefaultProviders => Globals.IsTestNet ? TestnetDefaultProviders : MainnetDefaultProviders;

        /// <summary>
        /// The three provider settings with the ones the configuration left out filled from <see cref="DefaultProviders"/>:
        /// each takes the next default whose address no setting holds yet. A setting the operator wrote is kept as it is.
        /// </summary>
        public static string[] WithDefaults(IReadOnlyList<string?> configured)
        {
            var filled = configured.Select(c => string.IsNullOrWhiteSpace(c) ? "" : c!.Trim()).ToArray();
            var held = new HashSet<string>(filled.Where(f => f.Length > 0).Select(AddressOf), StringComparer.OrdinalIgnoreCase);
            var spare = new Queue<string>(DefaultProviders.Where(d => !held.Contains(AddressOf(d))));
            for (var i = 0; i < filled.Length; i++)
                if (filled[i].Length == 0 && spare.Count > 0)
                    filled[i] = spare.Dequeue();
            return filled;
        }

        /// <summary>
        /// What the start check found when fewer than <see cref="RequiredAgreement"/> providers serve this network's
        /// Base chain; null when enough do, and before the check has run.
        /// </summary>
        public static string? StartWarning { get; private set; }

        /// <summary>The configured providers, in the order they are asked.</summary>
        public static IReadOnlyList<string> Providers() => BaseBridgeService.RpcUrlCandidates().ToList();

        /// <summary>
        /// The configured providers as they count toward agreement: one for each address (host and port). The same
        /// address written twice, or under two keys or paths, is one provider and cannot agree with itself.
        /// </summary>
        public static IReadOnlyList<string> DistinctProviders() =>
            Providers().GroupBy(AddressOf, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        private static string AddressOf(string url)
        {
            try { return new Uri(url).Authority; }
            catch { return url; }
        }

        // ── The clients ──────────────────────────────────────────────────────────────────

        private static Web3 Client(string url) => new Web3(url);

        private static Web3 Client(Account account, string url) => new Web3(account, url);

        // ── The chain id ─────────────────────────────────────────────────────────────────

        /// <summary>Whether a chain id (a burn event's own, or a provider's) is this network's Base chain.</summary>
        public static bool IsExpectedChain(BigInteger chainId) => chainId == new BigInteger(Globals.BaseEvmChainId);

        /// <summary>
        /// Whether a chain id written in the configuration is taken. The network decides which Base chain its bridge is
        /// on (<see cref="Globals.BaseEvmChainId"/>); any other id is refused and the network's is used.
        /// </summary>
        public static bool AcceptsConfiguredChainId(long configured)
        {
            if (configured == Globals.BaseEvmChainId) return true;
            ErrorLogUtility.LogError(
                $"[BaseBridge] BaseBridgeChainId={configured} is refused: this network's bridge is on Base chain {Globals.BaseEvmChainId}, and that id is used.",
                "BaseRpc.AcceptsConfiguredChainId");
            return false;
        }

        /// <summary>The provider as it is named in a log: its host, never its path or query (a key may be in them).</summary>
        internal static string NameOf(string url)
        {
            try { return new Uri(url).Host; }
            catch { return "(unreadable address)"; }
        }

        private static string CheckKey(string url) => Globals.BaseEvmChainId + "|" + url;

        /// <summary>
        /// Whether <paramref name="url"/> serves this network's Base chain, by its own eth_chainId. The answer is kept:
        /// <see cref="VerifiedForSeconds"/> for a provider on Base, <see cref="SetAsideSeconds"/> for one on another
        /// chain. A provider that does not answer is not judged, and is asked again at the next read.
        /// </summary>
        internal static async Task<(BaseProviderCheck Check, string Reason)> CheckChainAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return (BaseProviderCheck.NoAnswer, NoProviderReason);
            var key = CheckKey(url);
            var now = Clock();
            if (ChainChecks.TryGetValue(key, out var kept) && now - kept.At < (kept.OnBase ? VerifiedForSeconds : SetAsideSeconds))
                return kept.OnBase ? (BaseProviderCheck.OnBase, "") : (BaseProviderCheck.OtherChain, OtherChainReason(kept.Found));

            BigInteger found;
            try { found = (await Client(url).Eth.ChainId.SendRequestAsync()).Value; }
            catch (Exception ex) { return (BaseProviderCheck.NoAnswer, $"the provider did not give its chain id: {ex.Message}"); }

            var onBase = IsExpectedChain(found);
            var was = ChainChecks.TryGetValue(key, out var before) ? before : null;
            ChainChecks[key] = new ChainCheck(now, onBase, found.ToString());
            if (!onBase && was is not { OnBase: false })
                ErrorLogUtility.LogError(
                    $"[BaseBridge] Provider {NameOf(url)} serves chain {found}, not Base chain {Globals.BaseEvmChainId}. It is not asked; its chain id is checked again every {SetAsideSeconds} s.",
                    "BaseRpc.CheckChainAsync");
            else if (onBase && was is { OnBase: false })
                LogUtility.Log($"[BaseBridge] Provider {NameOf(url)} serves Base chain {Globals.BaseEvmChainId} again and is asked again.", "BaseRpc.CheckChainAsync");
            return onBase ? (BaseProviderCheck.OnBase, "") : (BaseProviderCheck.OtherChain, OtherChainReason(found.ToString()));
        }

        private static string OtherChainReason(string found) =>
            $"the provider serves chain {found}, not Base chain {Globals.BaseEvmChainId}";

        /// <summary>
        /// Asks every configured provider for its chain id when the node starts, and logs each: one on another chain is
        /// set aside from the first read on, not only once a read meets it.
        /// </summary>
        public static async Task<IReadOnlyList<(string Url, BaseProviderCheck Check)>> CheckProvidersAtStartAsync()
        {
            var results = new List<(string Url, BaseProviderCheck Check)>();
            foreach (var url in Providers())
            {
                var (check, reason) = await CheckChainAsync(url);
                results.Add((url, check));
                if (check == BaseProviderCheck.OnBase)
                    LogUtility.Log($"[BaseBridge] Provider {NameOf(url)} serves Base chain {Globals.BaseEvmChainId}.", "BaseRpc.CheckProvidersAtStartAsync");
                else if (check == BaseProviderCheck.NoAnswer)
                    LogUtility.Log($"[BaseBridge] Provider {NameOf(url)} was not reached at start ({reason}); it is asked again when Base is next read.", "BaseRpc.CheckProvidersAtStartAsync");
            }
            // What decides something of value needs RequiredAgreement providers, at different addresses, that give the
            // same answer. With fewer usable the node reads nothing of that kind, and its operator is told at once.
            var usable = results.Where(r => r.Check == BaseProviderCheck.OnBase).Select(r => AddressOf(r.Url)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            StartWarning = usable >= RequiredAgreement ? null
                : $"[BaseBridge] Only {usable} of the {results.Count} configured Base providers can be read from, and {RequiredAgreement} that agree are needed. "
                  + "Until then this node gives no vote on bridge exits, admits none, and signs nothing that rests on the Base contract. "
                  + "Set BaseBridgeRpcUrl, BaseBridgeRpcUrl2 and BaseBridgeRpcUrl3 in config.txt to three providers of Base run by different parties.";
            if (StartWarning != null)
            {
                ErrorLogUtility.LogError(StartWarning, "BaseRpc.CheckProvidersAtStartAsync");
                Console.WriteLine(StartWarning);
            }
            return results;
        }

        /// <summary>Test seam: forget every provider's chain-id check.</summary>
        internal static void ForgetChainChecksForTests() => ChainChecks.Clear();

        // ── Authorizing reads ────────────────────────────────────────────────────────────

        /// <summary>
        /// An authorizing read of contract state. Every provider is asked at once; <paramref name="read"/> is given the
        /// provider's client and the block to read at (the provider's finalized block, one block for every call of the
        /// read). The answer is the one <see cref="RequiredAgreement"/> providers give alike, <paramref name="same"/>
        /// naming what makes two answers the same; until then there is none. A provider that throws, or names no
        /// finalized block, has no answer.
        /// </summary>
        public static Task<BaseAnswer<T>> AuthorizingAgreedAsync<T>(Func<Web3, BlockParameter, Task<T>> read, Func<T, string> same) =>
            AgreedAsync<T>(async (web3, block) =>
            {
                var value = await read(web3, At(block));
                return BaseAnswer<(T Value, string Key)>.Yes((value, same(value)));
            });

        /// <summary>
        /// An authorizing read of a transaction's receipt. Every provider is asked at once. A provider's receipt is handed
        /// to <paramref name="judge"/> only from a block at or below that provider's finalized block whose hash is the
        /// one its chain holds at that height; <paramref name="judge"/> says what the receipt answers. The answer is the
        /// one <see cref="RequiredAgreement"/> providers give alike, from a receipt in the same block
        /// (<paramref name="same"/> names what makes two answers the same): "it is not the burn" needs that agreement as
        /// much as "it is". Until then there is no answer, for <see cref="NoAgreement"/> or, when no provider had a
        /// receipt to judge, the reason one gave (<see cref="ReceiptNotFound"/>, <see cref="ReceiptNotFinal"/>,
        /// <see cref="ReceiptOffChain"/>).
        /// </summary>
        public static Task<BaseAnswer<T>> AuthorizingReceiptAsync<T>(string txHash, Func<TransactionReceipt, BaseAnswer<T>> judge, Func<T, string> same) =>
            AgreedAsync<T>(async (web3, final) =>
            {
                var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                if (receipt == null) return BaseAnswer<(T Value, string Key)>.No(ReceiptNotFound);
                if (receipt.BlockNumber == null || receipt.BlockNumber.Value > final.Number) return BaseAnswer<(T Value, string Key)>.No(ReceiptNotFinal);
                var held = await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(receipt.BlockNumber));
                if (held == null || string.IsNullOrEmpty(held.BlockHash) || !string.Equals(held.BlockHash, receipt.BlockHash, StringComparison.OrdinalIgnoreCase))
                    return BaseAnswer<(T Value, string Key)>.No(ReceiptOffChain);
                var judged = judge(receipt);
                return judged.Answered
                    ? BaseAnswer<(T Value, string Key)>.Yes((judged.Value!, receipt.BlockHash.ToLowerInvariant() + "|" + same(judged.Value!)))
                    : BaseAnswer<(T Value, string Key)>.No(judged.Reason);
            });

        /// <summary>
        /// An authorizing read by a caller that reads one named provider itself (through <see cref="AuthorizingAtAsync"/>):
        /// every one of <paramref name="providers"/> is asked at once with <paramref name="read"/>, and the answer is the
        /// one <see cref="RequiredAgreement"/> of them give alike. A provider whose read throws gives none.
        /// </summary>
        public static Task<BaseAnswer<T>> AgreedAmongAsync<T>(IEnumerable<string> providers, Func<string, Task<T>> read, Func<T, string> same) =>
            AgreeAsync(providers.Select(async provider =>
            {
                try
                {
                    var value = await read(provider);
                    return BaseAnswer<(T Value, string Key)>.Yes((value, same(value)));
                }
                catch (Exception ex) { return BaseAnswer<(T Value, string Key)>.No(ex.Message); }
            }));

        /// <summary>
        /// An authorizing read of the newest block, asked of the providers in their configured order until one answers.
        /// Only for a rule that was written for the newest block and one provider and is also judged in a block: its
        /// answers stay as they were. Every other authorizing read takes what is final and agreed.
        /// </summary>
        public static Task<BaseAnswer<T>> AuthorizingNewestAskAsync<T>(Func<Web3, Task<BaseAnswer<T>>> ask) =>
            FirstAsync(ask);

        /// <summary>What one provider made of a read the burn watch asked several providers for.</summary>
        /// <param name="Provider">The provider, as configured.</param>
        /// <param name="Read">Whether it answered the read.</param>
        /// <param name="ProviderAtFault">
        /// When it did not: whether that was the provider's fault (it is not on Base, or what it served is not Base's)
        /// rather than a failure of the read itself.
        /// </param>
        public sealed record BaseProviderRead<T>(string Provider, bool Read, T? Value, bool ProviderAtFault, string Reason);

        /// <summary>
        /// The providers the burn watch can read from now, each with its finalized block: on Base, and naming one. The
        /// watch reads to the newest block that <see cref="RequiredAgreement"/> of them hold final.
        /// </summary>
        public static async Task<IReadOnlyList<(string Provider, BaseBlock Final)>> AuthorizingWatchProvidersAsync()
        {
            var asked = await Task.WhenAll(DistinctProviders().Select(async url =>
            {
                try
                {
                    var (check, _) = await CheckChainAsync(url);
                    if (check != BaseProviderCheck.OnBase) return (Provider: url, Final: (BaseBlock?)null);
                    return (Provider: url, Final: await FinalizedAsync(Client(url)));
                }
                catch { return (Provider: url, Final: (BaseBlock?)null); }
            }));
            return asked.Where(a => a.Final != null).Select(a => (a.Provider, a.Final!.Value)).ToList();
        }

        /// <summary>
        /// A read of blocks the caller names (the burn watch's ranges, no newer than the providers' finalized blocks),
        /// asked of each of <paramref name="providers"/> for itself, at once. Nothing is thrown: each provider's
        /// answer, or why it has none, is returned.
        /// </summary>
        public static async Task<IReadOnlyList<BaseProviderRead<T>>> AuthorizingEachAsync<T>(IEnumerable<string> providers, Func<Web3, Task<T>> read) =>
            await Task.WhenAll(providers.Select(async url =>
            {
                try
                {
                    var (check, reason) = await CheckChainAsync(url);
                    if (check != BaseProviderCheck.OnBase) return new BaseProviderRead<T>(url, false, default, true, reason);
                    return new BaseProviderRead<T>(url, true, await read(Client(url)), false, "");
                }
                catch (BaseProviderException ex) { return new BaseProviderRead<T>(url, false, default, true, ex.Message); }
                catch (Exception ex) { return new BaseProviderRead<T>(url, false, default, false, ex.Message); }
            }));

        /// <summary>
        /// An authorizing read of contract state asked of one provider the caller names (a caller that walks the
        /// providers itself). <paramref name="read"/> is given the block to read at: that provider's finalized block,
        /// one block for every call of the read. Throws when the provider is not on Base or names no finalized block,
        /// or when the read does.
        /// </summary>
        public static async Task<T> AuthorizingAtAsync<T>(string url, Func<Web3, BlockParameter, Task<T>> read)
        {
            var (check, reason) = await CheckChainAsync(url);
            if (check != BaseProviderCheck.OnBase) throw new BaseProviderException($"Base cannot be read: {reason}");
            var web3 = Client(url);
            var final = await FinalizedAsync(web3) ?? throw new BaseProviderException($"Base cannot be read: {NoFinalizedBlock}");
            return await read(web3, At(final));
        }

        // ── Advisory reads ───────────────────────────────────────────────────────────────

        /// <summary>An advisory read, asked of the providers in their configured order until one does not throw.</summary>
        public static Task<BaseAnswer<T>> AdvisoryFirstAsync<T>(Func<Web3, Task<T>> read) =>
            FirstAsync<T>(async web3 => BaseAnswer<T>.Yes(await read(web3)));

        /// <summary>
        /// An advisory read asked of the first configured provider only. Throws when that provider is not on Base, or
        /// when the read does.
        /// </summary>
        public static Task<T> AdvisoryPrimaryAsync<T>(Func<Web3, Task<T>> read) =>
            PrimaryAsync(read);

        // ── Sending ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A client that signs with <paramref name="account"/>, for a transaction this node sends to Base through the
        /// first configured provider and then waits on. What the transaction does is judged by the contract; what is
        /// read back through this client (gas, its receipt) is advisory. Throws when that provider is not on Base.
        /// </summary>
        public static async Task<Web3> SenderAsync(Account account)
        {
            var url = BaseBridgeService.BaseRpcUrl;
            var (check, reason) = await CheckChainAsync(url);
            if (check != BaseProviderCheck.OnBase) throw new BaseProviderException($"Base cannot be sent to: {reason}");
            return Client(account, url);
        }

        // ── How the providers are asked ──────────────────────────────────────────────────

        /// <summary>The tag a provider reads as its finalized block.</summary>
        private static BlockParameter FinalizedTag()
        {
            var tag = new BlockParameter();
            tag.SetValue(BlockParameter.BlockParameterType.finalized);
            return tag;
        }

        /// <summary>The block to read contract state at: <paramref name="block"/> by its number.</summary>
        private static BlockParameter At(BaseBlock block) => new((ulong)block.Number);

        /// <summary>The provider's finalized block, or null when it names none.</summary>
        private static async Task<BaseBlock?> FinalizedAsync(Web3 web3)
        {
            var block = await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(FinalizedTag());
            if (block?.Number == null || string.IsNullOrEmpty(block.BlockHash)) return null;
            return new BaseBlock((long)block.Number.Value, block.BlockHash);
        }

        /// <summary>Asks the providers in their order, of the newest block, until one answers.</summary>
        private static async Task<BaseAnswer<T>> FirstAsync<T>(Func<Web3, Task<BaseAnswer<T>>> ask)
        {
            var last = NoProviderReason;
            foreach (var url in Providers())
            {
                try
                {
                    var (check, reason) = await CheckChainAsync(url);
                    if (check != BaseProviderCheck.OnBase) { last = reason; continue; }
                    var answer = await ask(Client(url));
                    if (answer.Answered) return answer;
                    last = answer.Reason;
                }
                catch (Exception ex) { last = ex.Message; }
            }
            return BaseAnswer<T>.No(last);
        }

        /// <summary>One provider's answer to an authorizing read, at its finalized block; no answer when it throws.</summary>
        private static async Task<BaseAnswer<(T Value, string Key)>> AskFinalAsync<T>(string url, Func<Web3, BaseBlock, Task<BaseAnswer<(T Value, string Key)>>> ask)
        {
            try
            {
                var (check, reason) = await CheckChainAsync(url);
                if (check != BaseProviderCheck.OnBase) return BaseAnswer<(T Value, string Key)>.No(reason);
                var web3 = Client(url);
                if (await FinalizedAsync(web3) is not { } finalized) return BaseAnswer<(T Value, string Key)>.No(NoFinalizedBlock);
                return await ask(web3, finalized);
            }
            catch (Exception ex) { return BaseAnswer<(T Value, string Key)>.No(ex.Message); }
        }

        /// <summary>
        /// Asks every provider at once, each at its own finalized block, and answers as soon as
        /// <see cref="RequiredAgreement"/> of them have given the same answer (the same key). When all have been heard
        /// and no answer has that many, there is none: <see cref="NoAgreement"/> when a provider did answer, otherwise
        /// the reason a provider gave for having none.
        /// </summary>
        private static Task<BaseAnswer<T>> AgreedAsync<T>(Func<Web3, BaseBlock, Task<BaseAnswer<(T Value, string Key)>>> ask) =>
            AgreeAsync(DistinctProviders().Select(url => AskFinalAsync(url, ask)));

        /// <summary>The answer <see cref="RequiredAgreement"/> of the providers' answers share (the same key), as soon as that many have.</summary>
        private static async Task<BaseAnswer<T>> AgreeAsync<T>(IEnumerable<Task<BaseAnswer<(T Value, string Key)>>> answers)
        {
            var pending = answers.ToList();
            var given = new Dictionary<string, int>(StringComparer.Ordinal);
            var answered = 0;
            var last = NoProviderReason;
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var answer = await done;
                if (!answer.Answered) { last = answer.Reason; continue; }
                answered++;
                var count = given.TryGetValue(answer.Value.Key, out var n) ? n + 1 : 1;
                if (count >= RequiredAgreement) return BaseAnswer<T>.Yes(answer.Value.Value);
                given[answer.Value.Key] = count;
            }
            return BaseAnswer<T>.No(answered == 0 ? last : $"{NoAgreement} ({answered} answered)");
        }

        private static async Task<T> PrimaryAsync<T>(Func<Web3, Task<T>> read)
        {
            var url = BaseBridgeService.BaseRpcUrl;
            var (check, reason) = await CheckChainAsync(url);
            if (check != BaseProviderCheck.OnBase) throw new BaseProviderException($"Base cannot be read: {reason}");
            return await read(Client(url));
        }
    }
}
