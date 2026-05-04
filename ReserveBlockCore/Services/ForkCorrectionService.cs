using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// PHASE 2: Handles fork correction messages from casters.
    /// When a caster detects a hash split among peer casters and votes on the canonical
    /// block (3/5 majority), it broadcasts the correction to all connected validators.
    /// This service processes those corrections on the validator side.
    /// </summary>
    public static class ForkCorrectionService
    {
        /// <summary>Prevents concurrent fork corrections.</summary>
        private static int _correctionInProgress; // 0 = idle, 1 = running

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

                    await ApplyCorrectionAsync(height, correctBlock, casterIP);
                }
                // Case 2: The correction is for a height we haven't reached yet — just queue it
                else if (height == myHeight + 1)
                {
                    // This is the next expected block — add to download dict for normal processing
                    BlockDownloadService.BlockDict.AddOrUpdate(
                        correctBlock.Height,
                        new List<(Block block, string IPAddress)> { (correctBlock, casterIP) },
                        (key, existingList) =>
                        {
                            existingList.Add((correctBlock, casterIP));
                            return existingList;
                        });

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
                        $"Rolling back {myHeight - height + 1} block(s).",
                        "ForkCorrectionService");

                    var blocksToRollback = (int)(myHeight - height + 1);
                    await ApplyDeepCorrectionAsync(height, correctBlock, blocksToRollback, casterIP);
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
        /// Applies a fork correction by rolling back the current tip and applying the correct block.
        /// Used when the correction is for our current height.
        /// </summary>
        private static async Task ApplyCorrectionAsync(long height, Block correctBlock, string casterIP)
        {
            if (Interlocked.CompareExchange(ref _correctionInProgress, 1, 0) != 0)
            {
                LogUtility.Log("[ForkCorrection] Correction already in progress, skipping.", "ForkCorrectionService");
                return;
            }

            try
            {
                var wasResyncing = Globals.IsResyncing;
                Globals.IsResyncing = true;

                try
                {
                    // Rollback the bad block (1 block from tip)
                    var rollbackResult = await BlockRollbackUtility.RollbackBlocksFast(1, manageIsResyncing: false);
                    if (!rollbackResult)
                    {
                        LogUtility.Log("[ForkCorrection] Rollback failed.", "ForkCorrectionService");
                        return;
                    }

                    // Apply the correct block
                    var validateResult = await BlockValidatorService.ValidateBlock(correctBlock, false, false, false);
                    
                    LogUtility.Log(
                        $"[ForkCorrection] Correction applied for height {height}: success={validateResult}. " +
                        $"New tip: height={Globals.LastBlock.Height} hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}",
                        "ForkCorrectionService");
                    ConsoleWriterService.Output(
                        $"[ForkCorrection] Applied correction from caster {casterIP} at height {height}.");

                    // Re-download any subsequent blocks
                    if (validateResult)
                    {
                        try { await BlockDownloadService.GetAllBlocks(); } catch { }
                    }
                }
                finally
                {
                    Globals.IsResyncing = wasResyncing;
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[ForkCorrection] Error during correction: {ex.Message}", "ForkCorrectionService");
            }
            finally
            {
                Interlocked.Exchange(ref _correctionInProgress, 0);
            }
        }

        /// <summary>
        /// Applies a deep fork correction by rolling back multiple blocks.
        /// Used when the correction is for a height below our current tip.
        /// </summary>
        private static async Task ApplyDeepCorrectionAsync(long height, Block correctBlock, int blocksToRollback, string casterIP)
        {
            if (Interlocked.CompareExchange(ref _correctionInProgress, 1, 0) != 0)
            {
                LogUtility.Log("[ForkCorrection] Correction already in progress, skipping.", "ForkCorrectionService");
                return;
            }

            try
            {
                // Safety: don't roll back too many blocks from a single correction message
                if (blocksToRollback > 5)
                {
                    LogUtility.Log(
                        $"[ForkCorrection] Deep correction requires {blocksToRollback} blocks — too deep. " +
                        $"Max allowed is 5. Ignoring to prevent potential attack.",
                        "ForkCorrectionService");
                    return;
                }

                var wasResyncing = Globals.IsResyncing;
                Globals.IsResyncing = true;

                try
                {
                    var rollbackResult = await BlockRollbackUtility.RollbackBlocksFast(blocksToRollback, manageIsResyncing: false);
                    if (!rollbackResult)
                    {
                        LogUtility.Log("[ForkCorrection] Deep rollback failed.", "ForkCorrectionService");
                        return;
                    }

                    // Apply the correct block
                    var validateResult = await BlockValidatorService.ValidateBlock(correctBlock, false, false, false);

                    LogUtility.Log(
                        $"[ForkCorrection] Deep correction applied for height {height}: success={validateResult}. " +
                        $"Rolled back {blocksToRollback} block(s). " +
                        $"New tip: height={Globals.LastBlock.Height} hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}",
                        "ForkCorrectionService");

                    // Re-download subsequent blocks from peers
                    if (validateResult)
                    {
                        try { await BlockDownloadService.GetAllBlocks(); } catch { }
                    }
                }
                finally
                {
                    Globals.IsResyncing = wasResyncing;
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[ForkCorrection] Error during deep correction: {ex.Message}", "ForkCorrectionService");
            }
            finally
            {
                Interlocked.Exchange(ref _correctionInProgress, 0);
            }
        }
    }
}