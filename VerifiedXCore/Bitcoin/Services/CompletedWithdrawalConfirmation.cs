using System.Collections.Concurrent;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Fund-loss audit item 8 (validator-local). A vBTC V2 owner's spendable balance is the deposit address's confirmed
    /// BTC plus the owner ledger, which adds back every Completed withdrawal because its escrow debit is already
    /// reflected in the deposit once the Bitcoin transaction confirms. Between the COMPLETE transaction (sent right after
    /// broadcast; it accepts any 64-hex txid) and that confirmation, the deposit still holds the withdrawn coin, so the
    /// owner was overstated by the withdrawn amount - for as long as the owner cared to withhold the Bitcoin
    /// transaction. Block validation never asks Bitcoin (it trusts the producer for owner debits), so the only place
    /// this can be enforced is where validators admit and propose: there, a Completed withdrawal whose Bitcoin
    /// transaction is not yet confirmed is NOT added back.
    ///
    /// Confirmed txids are remembered for the life of the process (a confirmation never goes away), so each Completed
    /// withdrawal costs one Electrum answer. No answer for an unconfirmed candidate is reported as null: the caller
    /// fails closed exactly as it does when the deposit balance itself cannot be read.
    /// </summary>
    public static class CompletedWithdrawalConfirmation
    {
        /// <summary>Confirmations of a Bitcoin txid: null = no answer, 0 = known and unconfirmed, &gt;= 1 confirmed. Replaceable for tests.</summary>
        internal static Func<string, Task<int?>> LookupConfirmations = async txid =>
            (await BitcoinTransactionService.GetTransactionConfirmationsResilient(txid)).Confirmations;

        private static readonly ConcurrentDictionary<string, byte> _confirmed = new(StringComparer.OrdinalIgnoreCase);

        internal static void ResetForTests() => _confirmed.Clear();

        /// <summary>The Completed withdrawals on <paramref name="scUID"/> that the owner add-back counts, with their Bitcoin txids.</summary>
        public static List<(string BtcTxId, decimal Amount)> Candidates(string scUID, long currentHeight)
        {
            var rows = VBTCWithdrawalRequest.GetCompletedWithdrawalRows(scUID, currentHeight);
            return rows.Where(r => !string.IsNullOrWhiteSpace(r.BTCTxHash))
                .Select(r => (r.BTCTxHash!, r.Amount))
                .ToList();
        }

        /// <summary>
        /// Sum of the Completed withdrawals on <paramref name="scUID"/> whose Bitcoin transaction is not confirmed (unconfirmed
        /// or unknown to the servers), or null when a lookup had no answer. 0 when there is nothing to check.
        /// </summary>
        public static async Task<decimal?> UnconfirmedAmountAsync(string scUID, long currentHeight)
        {
            List<(string BtcTxId, decimal Amount)> candidates;
            try { candidates = Candidates(scUID, currentHeight); }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Completed-withdrawal candidates for {scUID} could not be read: {ex.Message}", "CompletedWithdrawalConfirmation.UnconfirmedAmountAsync()");
                return null;
            }
            return await UnconfirmedAmountAsync(candidates).ConfigureAwait(false);
        }

        /// <summary>The same, over an explicit candidate list (the unit-testable core).</summary>
        public static async Task<decimal?> UnconfirmedAmountAsync(IEnumerable<(string BtcTxId, decimal Amount)> candidates)
        {
            decimal unconfirmed = 0M;
            foreach (var (txid, amount) in candidates)
            {
                if (_confirmed.ContainsKey(txid))
                    continue;
                int? confirmations;
                try { confirmations = await LookupConfirmations(txid).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Confirmation lookup for completed withdrawal tx {txid} failed: {ex.Message}", "CompletedWithdrawalConfirmation.UnconfirmedAmountAsync()");
                    confirmations = null;
                }
                if (confirmations == null)
                    return null; // no answer: the caller fails closed
                if (confirmations.Value >= 1)
                {
                    _confirmed[txid] = 1;
                    continue;
                }
                unconfirmed += amount; // known but unconfirmed (mempool, or withheld and unknown to every server)
            }
            return unconfirmed;
        }
    }
}
