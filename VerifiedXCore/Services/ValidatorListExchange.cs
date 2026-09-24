using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// VX-15: rules for the caster-to-caster validator list exchange (ValidatorController.ExchangeValidatorList and
    /// the client side in BlockcasterNode.SyncValidatorListsWithPeersAsync).
    ///
    /// Before: the route accepted a list from anyone who named a public caster address, and both directions merged
    /// every entry straight into Globals.NetworkValidators as fully trusted, with the sender's FirstSeenAtHeight.
    /// Membership of that registry is itself a privilege (block submission by IP, block requests, gossip, unban),
    /// and trusted entries feed the caster candidate pool.
    ///
    /// Now:
    ///  1. Message: signed by the sending caster over VFX_VALLIST_V1 (caster, timestamp, hash of every field of every
    ///     entry); the sender must be in the current committee; fresh (±90 s); a request signature is single-use.
    ///  2. Entry: the PublicKey must derive the Address, and the address must hold the validator balance.
    ///  3. Trust is local: an entry waits in a pending store (never in NetworkValidators) until
    ///     <see cref="RequiredCorroboration"/> distinct committee casters have reported the same (IP, key), then must
    ///     pass the liveness check. It joins with FirstSeenAtHeight = the local tip. The wire FirstSeenAtHeight and
    ///     LastSeen are ignored.
    /// </summary>
    public static class ValidatorListExchange
    {
        public const int MaxEntriesPerMessage = 2000;
        public const long PendingTtlSeconds = 3600;
        public const int MaxPendingAddresses = 5000;
        /// <summary>Liveness checks (promotions) one message may trigger; the rest wait for the next sync.</summary>
        public const int PromotionBudgetPerMessage = 25;

        public enum OfferResult { Rejected, Known, Pending, Deferred, Promoted, Unreachable, Full }

        private sealed class Claim
        {
            public readonly HashSet<string> Casters = new(StringComparer.Ordinal);
            public long FirstOffered;
        }

        // address -> ("ip|publicKey" -> claim)
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Claim>> _pending = new(StringComparer.Ordinal);

        /// <summary>SHA-256 (lower hex) over every field of every entry, in order.</summary>
        public static string ComputeEntriesHash(IEnumerable<ValidatorListEntry>? entries)
        {
            var sb = new StringBuilder();
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    if (e == null) { sb.Append("null\n"); continue; }
                    sb.Append(e.Address).Append('|').Append(e.IPAddress).Append('|').Append(e.PublicKey).Append('|')
                      .Append(e.FirstSeenAtHeight).Append('|').Append(e.LastSeen).Append('\n');
                }
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }

        public static string SigningMessage(string casterAddress, long timestampUnix, IEnumerable<ValidatorListEntry>? entries) =>
            ConsensusMessageFormatter.FormatValidatorListV1(casterAddress, timestampUnix, ComputeEntriesHash(entries));

        /// <summary>Signs this node's outgoing list (request or response) with its validator key.</summary>
        public static (long Timestamp, string Signature) SignOwn(List<ValidatorListEntry> entries)
        {
            var ts = TimeUtil.GetTime();
            var sig = SignatureService.ValidatorSignature(SigningMessage(Globals.ValidatorAddress ?? "", ts, entries));
            return (ts, sig ?? "");
        }

        private static bool IsCommitteeCaster(string? address) =>
            !string.IsNullOrEmpty(address) && Globals.BlockCasters.ToList().Any(c => c.ValidatorAddress == address);

        /// <summary>
        /// Authenticates a list message. <paramref name="consumeSignature"/> is true for inbound requests (single use);
        /// responses to our own request are bound to that request by the fresh timestamp and the expected sender.
        /// </summary>
        public static bool VerifyMessage(string? casterAddress, long timestampUnix, string? signature, List<ValidatorListEntry>? entries,
            bool consumeSignature, out string reason)
        {
            reason = "";
            if (!IsCommitteeCaster(casterAddress)) { reason = "sender is not a committee caster"; return false; }
            if (entries != null && entries.Count > MaxEntriesPerMessage) { reason = $"too many entries ({entries.Count})"; return false; }
            var now = TimeUtil.GetTime();
            if (!ConsensusRequestAuth.IsFresh(timestampUnix, now)) { reason = "stale or missing timestamp"; return false; }
            if (!ConsensusRequestAuth.VerifySigner(casterAddress, SigningMessage(casterAddress!, timestampUnix, entries), signature))
            { reason = "bad or missing signature"; return false; }
            if (consumeSignature && !ConsensusRequestAuth.TryConsume(signature!, now)) { reason = "replayed signature"; return false; }
            return true;
        }

        /// <summary>Entry-level rules: key binds the address, the address holds the validator balance.</summary>
        public static bool IsAcceptableEntry(ValidatorListEntry? e, out string reason)
        {
            reason = "";
            if (e == null || string.IsNullOrEmpty(e.Address) || string.IsNullOrEmpty(e.IPAddress) || string.IsNullOrEmpty(e.PublicKey))
            { reason = "incomplete entry"; return false; }
            if (e.Address == Globals.ValidatorAddress) { reason = "self"; return false; }
            if (!ConsensusRequestAuth.PublicKeyMatchesAddress(e.PublicKey, e.Address)) { reason = "public key does not derive the address"; return false; }
            decimal balance;
            try { balance = AccountStateTrei.GetAccountBalance(e.Address); } catch { balance = 0M; }
            if (balance < ValidatorService.ValidatorRequiredAmount()) { reason = "below validator balance"; return false; }
            return true;
        }

        /// <summary>
        /// Distinct committee casters (other than this node) that must report the same entry: two, or every other
        /// caster when fewer exist (a two-caster committee has only one other voice).
        /// </summary>
        public static int RequiredCorroboration()
        {
            var others = Globals.BlockCasters.ToList()
                .Select(c => c.ValidatorAddress)
                .Where(a => !string.IsNullOrEmpty(a) && a != Globals.ValidatorAddress)
                .Distinct()
                .Count();
            return Math.Max(1, Math.Min(2, others));
        }

        /// <summary>
        /// Records one caster's report of an entry (already message-authenticated). Promotes it into
        /// Globals.NetworkValidators once corroborated and live. <paramref name="mayPromote"/> gates the liveness
        /// call (per-message / per-round budget); a corroborated claim that is not promoted now stays pending.
        /// </summary>
        public static async Task<OfferResult> OfferAsync(ValidatorListEntry entry, string casterAddress, Func<bool>? mayPromote = null)
        {
            if (!IsAcceptableEntry(entry, out _)) return OfferResult.Rejected;
            if (Globals.NetworkValidators.ContainsKey(entry.Address)) return OfferResult.Known;

            var now = TimeUtil.GetTime();
            PruneExpired(now);
            if (!_pending.ContainsKey(entry.Address) && _pending.Count >= MaxPendingAddresses) return OfferResult.Full;

            var claims = _pending.GetOrAdd(entry.Address, _ => new ConcurrentDictionary<string, Claim>(StringComparer.Ordinal));
            var claim = claims.GetOrAdd(entry.IPAddress + "|" + entry.PublicKey, _ => new Claim { FirstOffered = now });
            int count;
            lock (claim)
            {
                claim.Casters.Add(casterAddress);
                count = claim.Casters.Count;
            }
            if (count < RequiredCorroboration()) return OfferResult.Pending;
            if (mayPromote != null && !mayPromote()) return OfferResult.Deferred;

            bool live;
            try { live = await NetworkValidator.CheckValidatorLiveness(entry.IPAddress); }
            catch { live = false; }
            if (!live) return OfferResult.Unreachable;

            var nv = new NetworkValidator
            {
                Address = entry.Address,
                IPAddress = entry.IPAddress,
                PublicKey = entry.PublicKey,
                IsFullyTrusted = true, // local decision: corroborated by distinct committee casters + live
                LastSeen = TimeUtil.GetTime(),
                FirstSeenAtHeight = Globals.LastBlock?.Height ?? 0, // local fact, never the wire value
                CheckFailCount = 0,
            };
            if (!Globals.NetworkValidators.TryAdd(entry.Address, nv)) return OfferResult.Known;
            _pending.TryRemove(entry.Address, out _);
            return OfferResult.Promoted;
        }

        /// <summary>Test / diagnostics: casters that have reported this (address, ip, key).</summary>
        public static int PendingReports(string address, string ipAddress, string publicKey) =>
            _pending.TryGetValue(address, out var claims) && claims.TryGetValue(ipAddress + "|" + publicKey, out var c) ? c.Casters.Count : 0;

        public static void ClearPending() => _pending.Clear();

        private static void PruneExpired(long now)
        {
            foreach (var (address, claims) in _pending)
            {
                foreach (var (key, claim) in claims)
                    if (now - claim.FirstOffered > PendingTtlSeconds) claims.TryRemove(key, out _);
                if (claims.IsEmpty) _pending.TryRemove(address, out _);
            }
        }

        /// <summary>A budget shared by the concurrent per-peer tasks of one sync round.</summary>
        public sealed class Budget
        {
            private int _remaining;
            public Budget(int size) { _remaining = size; }
            public bool TryTake() => Interlocked.Decrement(ref _remaining) >= 0;
        }

        /// <summary>
        /// Client side: merges a peer caster's response. The response must be signed by the caster we asked.
        /// Returns the count of each outcome.
        /// </summary>
        public static async Task<Dictionary<OfferResult, int>> MergeResponseAsync(ValidatorListExchangeResponse? resp, string expectedCasterAddress, Budget budget)
        {
            var counts = new Dictionary<OfferResult, int>();
            if (resp == null || resp.CasterAddress != expectedCasterAddress)
                return counts;
            if (!VerifyMessage(resp.CasterAddress, resp.Timestamp, resp.Signature, resp.Validators, consumeSignature: false, out var why))
            {
                CasterLogUtility.Log($"VALLIST-SYNC: response from {expectedCasterAddress} rejected: {why}", "CONSENSUS");
                return counts;
            }
            foreach (var entry in resp.Validators ?? new List<ValidatorListEntry>())
            {
                var r = await OfferAsync(entry, expectedCasterAddress, budget.TryTake);
                counts[r] = counts.TryGetValue(r, out var n) ? n + 1 : 1;
            }
            return counts;
        }
    }
}
