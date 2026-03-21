using System.Diagnostics.CodeAnalysis;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Privacy
{
    /// <summary>Shared helpers for privacy REST controllers (Phase 7).</summary>
    public static class PrivacyApiHelper
    {
        public static bool TryGetKeyMaterial(ShieldedWallet w, string? walletPassword, [NotNullWhen(true)] out ShieldedKeyMaterial? keys, out string? error)
        {
            keys = null;
            error = null;
            if (w.IsViewOnly || w.SpendingKey == null || w.SpendingKey.Length == 0)
            {
                error = "Wallet is view-only or has no spending key.";
                return false;
            }
            if (!ShieldedWalletService.TryUnwrapSpendingBundle(w, walletPassword ?? "", out var sp, out var enc, out var uerr))
            {
                error = uerr ?? "Could not unwrap spending key (wrong password?).";
                return false;
            }
            if (!ShieldedAddressCodec.TryDecodeEncryptionKey(w.ShieldedAddress, out var pub33, out var derr) || pub33 == null)
            {
                error = derr ?? "Invalid zfx address.";
                return false;
            }
            keys = new ShieldedKeyMaterial
            {
                SpendingKey32 = sp,
                ViewingKey32 = w.ViewingKey,
                EncryptionPrivateKey32 = enc,
                EncryptionPublicKey33 = pub33,
                ZfxAddress = w.ShieldedAddress
            };
            return true;
        }

        public static async Task<(bool ok, string json)> BroadcastVerifiedPrivateTxAsync(Transaction tx)
        {
            var result = await TransactionValidatorService.VerifyTX(tx);
            if (!result.Item1)
                return (false, Newtonsoft.Json.JsonConvert.SerializeObject(new { Success = false, Message = result.Item2 }));

            if (tx.TransactionRating == null)
            {
                var rating = await TransactionRatingService.GetTransactionRating(tx);
                tx.TransactionRating = rating;
            }

            await TransactionData.AddToPool(tx);
            await P2PClient.SendTXMempool(tx);
            return (true, Newtonsoft.Json.JsonConvert.SerializeObject(new { Success = true, Message = "Broadcast.", Hash = tx.Hash }));
        }

        /// <summary>Approximate VFX shielded balance for co-shield UX warnings.</summary>
        public static decimal SumVfxUnspent(ShieldedWallet? w)
        {
            if (w?.UnspentCommitments == null)
                return 0;
            return w.UnspentCommitments
                .Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal))
                .Sum(c => c.Amount);
        }
    }
}
