using System;
using System.Collections.Generic;

namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// Tracks the state of an MPC (FROST DKG) ceremony for generating deposit addresses
    /// </summary>
    public class MPCCeremonyState
    {
        /// <summary>
        /// Unique identifier for this ceremony
        /// </summary>
        public string CeremonyId { get; set; }

        /// <summary>
        /// Current status of the ceremony
        /// </summary>
        public CeremonyStatus Status { get; set; }

        /// <summary>
        /// Owner address requesting the ceremony
        /// </summary>
        public string OwnerAddress { get; set; }

        /// <summary>
        /// Generated Bitcoin Taproot deposit address (result)
        /// </summary>
        public string? DepositAddress { get; set; }

        /// <summary>
        /// FROST group public key (result)
        /// </summary>
        public string? FrostGroupPublicKey { get; set; }

        /// <summary>
        /// DKG completion proof (result)
        /// </summary>
        public string? DKGProof { get; set; }

        /// <summary>
        /// Snapshot of validator addresses participating in ceremony
        /// </summary>
        public List<string> ValidatorSnapshot { get; set; }

        /// <summary>
        /// Required threshold for signing (percentage)
        /// </summary>
        public int RequiredThreshold { get; set; }

        /// <summary>
        /// Block height when DKG proof was generated
        /// </summary>
        public long ProofBlockHeight { get; set; }

        /// <summary>
        /// When the ceremony was initiated
        /// </summary>
        public long InitiatedTimestamp { get; set; }

        /// <summary>
        /// When the ceremony completed (or failed)
        /// </summary>
        public long? CompletedTimestamp { get; set; }

        /// <summary>
        /// Error message if ceremony failed
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Current round of DKG ceremony (1, 2, or 3)
        /// </summary>
        public int CurrentRound { get; set; }

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public int ProgressPercentage { get; set; }

        /// <summary>
        /// Additional metadata about ceremony progress
        /// </summary>
        public Dictionary<string, object> Metadata { get; set; }

        public MPCCeremonyState()
        {
            ValidatorSnapshot = new List<string>();
            Metadata = new Dictionary<string, object>();
            Status = CeremonyStatus.Initiated;
            ProgressPercentage = 0;
            CurrentRound = 0;
        }
    }

    /// <summary>
    /// Status of MPC ceremony
    /// </summary>
    public enum CeremonyStatus
    {
        /// <summary>
        /// Ceremony has been created and is waiting to start
        /// </summary>
        Initiated,

        /// <summary>
        /// Validating active validators
        /// </summary>
        ValidatingValidators,

        /// <summary>
        /// DKG Round 1: Commitment Phase in progress
        /// </summary>
        Round1InProgress,

        /// <summary>
        /// DKG Round 1 completed successfully
        /// </summary>
        Round1Complete,

        /// <summary>
        /// DKG Round 2: Share Distribution Phase in progress
        /// </summary>
        Round2InProgress,

        /// <summary>
        /// DKG Round 2 completed successfully
        /// </summary>
        Round2Complete,

        /// <summary>
        /// DKG Round 3: Verification Phase in progress
        /// </summary>
        Round3InProgress,

        /// <summary>
        /// DKG Round 3 completed successfully
        /// </summary>
        Round3Complete,

        /// <summary>
        /// Aggregating group public key
        /// </summary>
        AggregatingPublicKey,

        /// <summary>
        /// Generating DKG proof
        /// </summary>
        GeneratingProof,

        /// <summary>
        /// Ceremony completed successfully
        /// </summary>
        Completed,

        /// <summary>
        /// Ceremony failed
        /// </summary>
        Failed,

        /// <summary>
        /// Ceremony timed out
        /// </summary>
        TimedOut
    }
}
