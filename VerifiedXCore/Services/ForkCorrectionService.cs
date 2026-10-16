using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// PHASE 2: Handles fork correction messages from casters.
    /// When a caster detects a hash split among peer casters and votes on the canonical
    /// block (3/5 majority), it broadcasts the correction to all connected validators.
    /// This service processes those corrections on the validator side.
    /// </summary>
    public static class ForkCorrectionService
    {
        /// <summary>Replaceable for tests: whether the block carries a valid committee certificate (strict).</summary>
        internal static Func<Block, bool> HasValidCertificate = ConsensusCertificateVerifier.HasValidCertificate;

        /// <summary>
        /// Replaceable for tests: the bounded reorg onto the corrected block. It shares the recovery interlock, opens the
        /// download phase so the block is accepted, validates it fully and checks the result; the earlier in-house
        /// rollback left the node resyncing, so the replacement block was always refused and never applied.
        /// </summary>
        internal static Func<long, string, string, string, Block?, Task<bool>> Reorg = Utilities.ForkRecoveryUtility.ReorgToBranchAsync;

        /// <summary>
        /// Handles an incoming fork correction from a caster.
        /// If our block at the given height has a different hash, we rollback and apply
        /// the correct block. If hashes match, this is a no-op.
        /// </summary>
        /// <param name="height">The block height being corrected</param>
        /// <param name="blockJson">JSON-serialized correct block from caster majority vote</param>
        /// <param name="casterIP">IP of the caster that sent this correction</param>
        public static async Task HandleForkCorrectionAsync(long height, string blockJson, string casterIP)
        {
            try
            {
                // Wave 5 (interlock unification): exactly one rollback writer at any instant —
                // never interleave a pushed correction with an active RecoverAsync/ReorgToBranch.
                if (Utilities.ForkRecoveryUtility.IsRecoveryInProgress)
                {
                    LogUtility.Log($"[ForkCorrection] Skipping correction from {casterIP} — recovery/reorg already in progress.", "ForkCorrectionService");
                    return;
                }

                if (string.IsNullOrEmpty(blockJson))
                {
                    LogUtility.Log($"[ForkCorrection] Empty blockJson received from {casterIP}.", "ForkCorrectionService");
                    return;
                }

                var correctBlock = JsonConvert.DeserializeObject<Block>(blockJson);
                if (correctBlock == null)
                {
                    LogUtility.Log($"[ForkCorrection] Failed to deserialize block from {casterIP}.", "ForkCorrectionService");
                    return;
                }

                // Validate chain reference
                if (correctBlock.ChainRefId != BlockchainData.ChainRef)
                {
                    LogUtility.Log($"[ForkCorrection] Rejecting correction from {casterIP}: wrong ChainRefId.", "ForkCorrectionService");
                    return;
                }

                // Validate block signature before trusting it
                if (correctBlock.Height > 0)
                {
                    var sigValid = SignatureService.VerifySignature(
                        correctBlock.Validator, correctBlock.Hash, correctBlock.ValidatorSignature);
                    if (!sigValid)
                    {
                        LogUtility.Log(
                            $"[ForkCorrection] Rejecting correction from {casterIP}: invalid block signature for height {height}.",
                            "ForkCorrectionService");
                        return;
                    }
                }

                // The block must be the one the message names and must hash to its own hash: the rollback depth comes
                // from the height, and a signature over an arbitrary hash proves nothing about the block's content.
                if (correctBlock.Height != height || correctBlock.GetBlockHash() != correctBlock.Hash)
                {
                    LogUtility.Log($"[ForkCorrection] Rejecting correction from {casterIP}: block height or hash does not match (message height {height}).", "ForkCorrectionService");
                    return;
                }

                // The hash (and so the certificate) covers the merkle root, not the transactions: a certified header with
                // other transactions would roll the node back and only then fail validation.
                if (correctBlock.Transactions == null || correctBlock.NumOfTx != correctBlock.Transactions.Count
                    || correctBlock.MerkleRootOf() != correctBlock.MerkleRoot)
                {
                    LogUtility.Log($"[ForkCorrection] Rejecting correction from {casterIP}: the block's transactions do not match its header (height {height}).", "ForkCorrectionService");
                    return;
                }

                var myHeight = Globals.LastBlock.Height;

                // Case 1: The correction is for a block we already have — check if hash matches
                if (height == myHeight)
                {
                    if (Globals.LastBlock.Hash == correctBlock.Hash)
                    {
                        // We already have the correct block — no action needed
                        return;
                    }

                    // Our block hash differs from the caster majority — need to correct
                    LogUtility.Log(
                        $"[ForkCorrection] Hash divergence detected at height {height}. " +
                        $"Our hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]} " +
                        $"Correct hash={correctBlock.Hash?[..Math.Min(16, correctBlock.Hash?.Length ?? 0)]} " +
                        $"from caster {casterIP}. Initiating correction.",
                        "ForkCorrectionService");

                    await ReorgOntoAsync(correctBlock, casterIP);
                }
                // Case 2: The correction is for a height we haven't reached yet — just queue it
                else if (height == myHeight + 1)
                {
                    // This is the next expected block — add to download dict for normal processing. A certified one is the
                    // committee's approval for its height, as for a rollback below.
                    if (HasValidCertificate(correctBlock))
                        ApproveCertified(correctBlock);
                    BlockStaging.Stage(correctBlock, casterIP); // VX-19: de-duplicated staging

                    await BlockValidatorService.ValidateBlocks();
                }
                // Case 3: The correction is for a height below ours — we may need to rewind
                else if (height < myHeight && height > 0)
                {
                    // Check if our block at that height has the same hash
                    var ourBlock = BlockchainData.GetBlockByHeight(height);
                    if (ourBlock != null && ourBlock.Hash == correctBlock.Hash)
                    {
                        // We already have the correct block at this height
                        return;
                    }

                    LogUtility.Log(
                        $"[ForkCorrection] Correction for past height {height} (we're at {myHeight}). " +
                        $"Our hash at {height}={ourBlock?.Hash?[..Math.Min(16, ourBlock?.Hash?.Length ?? 0)]} " +
                        $"Correct hash={correctBlock.Hash?[..Math.Min(16, correctBlock.Hash?.Length ?? 0)]}. " +
                        $"Correcting {myHeight - height + 1} block(s).",
                        "ForkCorrectionService");

                    await ReorgOntoAsync(correctBlock, casterIP);
                }
                else
                {
                    // Correction is for a height too far ahead — ignore
                    LogUtility.Log(
                        $"[ForkCorrection] Ignoring correction from {casterIP} for height {height} (we're at {myHeight}).",
                        "ForkCorrectionService");
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[ForkCorrection] Error handling correction from {casterIP}: {ex.Message}", "ForkCorrectionService");
            }
        }

        /// <summary>
        /// The certificate is the committee's approval of the block: a hash a caster approved earlier for its height (or
        /// for the heights above it) would otherwise make validation refuse it.
        /// </summary>
        private static void ApproveCertified(Block certified)
        {
            Globals.CasterApprovedBlockHashDict[certified.Height] = certified.Hash;
            foreach (var stale in Globals.CasterApprovedBlockHashDict.Keys.Where(h => h > certified.Height).ToList())
                Globals.CasterApprovedBlockHashDict.TryRemove(stale, out _);
        }

        /// <summary>
        /// Rolls back to and adopts the corrected block, only when it carries a valid committee certificate: a block a
        /// single key signed made every recipient roll back. Without one, the fork is left to ForkChoice and recovery.
        /// </summary>
        private static async Task ReorgOntoAsync(Block correctBlock, string casterIP)
        {
            if (!HasValidCertificate(correctBlock))
            {
                LogUtility.Log($"[ForkCorrection] Not rolling back for the correction from {casterIP} at height {correctBlock.Height}: it carries no valid committee certificate.", "ForkCorrectionService");
                return;
            }
            ApproveCertified(correctBlock);

            var adopted = await Reorg(correctBlock.Height, correctBlock.Hash, casterIP, "ForkCorrection", correctBlock);
            LogUtility.Log($"[ForkCorrection] Correction at height {correctBlock.Height} from {casterIP}: {(adopted ? "adopted" : "not adopted")}.", "ForkCorrectionService");
        }
    }
}