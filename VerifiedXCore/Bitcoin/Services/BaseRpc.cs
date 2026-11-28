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
    /// A provider is asked only while it serves this network's Base chain (<see cref="Globals.BaseEvmChainId"/>, by its
    /// own eth_chainId). One that serves another chain is set aside for <see cref="SetAsideSeconds"/> and then asked
    /// again; none is set aside for good.
    /// </summary>
    public static class BaseRpc
    {
        /// <summary>The reason given when no provider is configured, or none was reached.</summary>
        public const string NoProviderReason = "no RPC reachable";

        /// <summary>Seconds a provider found on Base is used before its chain id is asked again.</summary>
        internal const int VerifiedForSeconds = 600;

        /// <summary>Seconds a provider found on another chain is left out before its chain id is asked again.</summary>
        internal const int SetAsideSeconds = 60;

        /// <summary>The clock the chain-id checks are timed by (seconds).</summary>
        internal static Func<long> Clock = () => TimeUtil.GetTime();

        private sealed record ChainCheck(long At, bool OnBase, string Found);

        private static readonly ConcurrentDictionary<string, ChainCheck> ChainChecks = new(StringComparer.Ordinal);

        /// <summary>The configured providers, in the order they are asked.</summary>
        public static IReadOnlyList<string> Providers() => BaseBridgeService.RpcUrlCandidates().ToList();

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
            return results;
        }

        /// <summary>Test seam: forget every provider's chain-id check.</summary>
        internal static void ForgetChainChecksForTests() => ChainChecks.Clear();

        // ── Authorizing reads ────────────────────────────────────────────────────────────

        /// <summary>
        /// An authorizing read, asked of the providers in their configured order until one answers. <paramref name="ask"/>
        /// is given the provider's client and the block to read contract state at, and says whether this provider has
        /// the answer; a provider that throws has none.
        /// </summary>
        public static Task<BaseAnswer<T>> AuthorizingAskAsync<T>(Func<Web3, BlockParameter, Task<BaseAnswer<T>>> ask) =>
            FirstAsync(ask);

        /// <summary>An authorizing read whose every answer is one: the first provider that does not throw gives it.</summary>
        public static Task<BaseAnswer<T>> AuthorizingFirstAsync<T>(Func<Web3, BlockParameter, Task<T>> read) =>
            FirstAsync<T>(async (web3, at) => BaseAnswer<T>.Yes(await read(web3, at)));

        /// <summary>
        /// An authorizing read asked of the first configured provider only. Throws when that provider is not on Base, or
        /// when the read does.
        /// </summary>
        public static Task<T> AuthorizingPrimaryAsync<T>(Func<Web3, BlockParameter, Task<T>> read) =>
            PrimaryAsync(read);

        // ── Advisory reads ───────────────────────────────────────────────────────────────

        /// <summary>An advisory read, asked of the providers in their configured order until one does not throw.</summary>
        public static Task<BaseAnswer<T>> AdvisoryFirstAsync<T>(Func<Web3, Task<T>> read) =>
            FirstAsync<T>(async (web3, _) => BaseAnswer<T>.Yes(await read(web3)));

        /// <summary>
        /// An advisory read asked of the first configured provider only. Throws when that provider is not on Base, or
        /// when the read does.
        /// </summary>
        public static Task<T> AdvisoryPrimaryAsync<T>(Func<Web3, Task<T>> read) =>
            PrimaryAsync<T>((web3, _) => read(web3));

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

        private static async Task<BaseAnswer<T>> FirstAsync<T>(Func<Web3, BlockParameter, Task<BaseAnswer<T>>> ask)
        {
            var last = NoProviderReason;
            foreach (var url in Providers())
            {
                try
                {
                    var (check, reason) = await CheckChainAsync(url);
                    if (check != BaseProviderCheck.OnBase) { last = reason; continue; }
                    var answer = await ask(Client(url), BlockParameter.CreateLatest());
                    if (answer.Answered) return answer;
                    last = answer.Reason;
                }
                catch (Exception ex) { last = ex.Message; }
            }
            return BaseAnswer<T>.No(last);
        }

        private static async Task<T> PrimaryAsync<T>(Func<Web3, BlockParameter, Task<T>> read)
        {
            var url = BaseBridgeService.BaseRpcUrl;
            var (check, reason) = await CheckChainAsync(url);
            if (check != BaseProviderCheck.OnBase) throw new BaseProviderException($"Base cannot be read: {reason}");
            return await read(Client(url), BlockParameter.CreateLatest());
        }
    }
}
