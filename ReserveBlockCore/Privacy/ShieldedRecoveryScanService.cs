using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Trial-decrypts optional per-output <see cref="PrivateShieldedOutput.EncryptedNoteB64"/> (Phase 2 wallet recovery helper).
    /// </summary>
    public static class ShieldedRecoveryScanService
    {
        /// <summary>Attempts to open every embedded note on outputs; wrong keys yield no entries (silent skip).</summary>
        public static IReadOnlyList<byte[]> TryDecryptNotes(PrivateTxPayload payload, ReadOnlySpan<byte> recipientEncryptionPrivateKey32)
        {
            var results = new List<byte[]>();
            foreach (var o in payload.Outs)
            {
                if (string.IsNullOrWhiteSpace(o.EncryptedNoteB64))
                    continue;
                byte[] raw;
                try
                {
                    raw = Convert.FromBase64String(o.EncryptedNoteB64);
                }
                catch
                {
                    continue;
                }
                if (ShieldedNoteEncryption.TryOpen(raw, recipientEncryptionPrivateKey32, out var plain, out _))
                    results.Add(plain!);
            }
            return results;
        }

        public static IEnumerable<(Block Block, Transaction Tx, byte[] Plaintext)> ScanBlocksForNotes(
            IEnumerable<Block> blocks,
            byte[] recipientEncryptionPrivateKey32)
        {
            foreach (var block in blocks)
            {
                if (block.Transactions == null)
                    continue;
                foreach (var tx in block.Transactions)
                {
                    if (tx?.Data == null || !PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _))
                        continue;
                    if (payload == null || !payload.TryValidateStructure(out _))
                        continue;
                    foreach (var plain in TryDecryptNotes(payload, recipientEncryptionPrivateKey32))
                        yield return (block, tx, plain);
                }
            }
        }
    }
}
