using ReserveBlockCore.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// Utility class for arbiter-related operations including deterministic lead arbiter selection
    /// </summary>
    public class ArbiterUtility
    {
        /// <summary>
        /// Deterministically selects a lead arbiter based on withdrawal unique ID and smart contract UID.
        /// This ensures all nodes independently arrive at the same lead arbiter selection.
        /// </summary>
        /// <param name="uniqueId">The unique identifier for the withdrawal request</param>
        /// <param name="scUID">The smart contract UID</param>
        /// <param name="arbiters">List of available arbiters</param>
        /// <returns>The deterministically selected lead arbiter, or null if no arbiters available</returns>
        public static Models.Arbiter? SelectLeadArbiter(string uniqueId, string scUID, List<Models.Arbiter> arbiters)
        {
            if (arbiters == null || !arbiters.Any())
                return null;

            // Sort arbiters deterministically by signing address
            var sortedArbiters = arbiters.OrderBy(x => x.SigningAddress).ToList();

            // Create deterministic hash from uniqueId + scUID
            var selectionInput = $"{uniqueId}{scUID}";
            var selectionHash = ProofUtility.CalculateSHA256Hash(selectionInput);
            
            // Convert hash to bytes
            var hashBytes = Convert.FromBase64String(selectionHash);
            
            // Use first 4 bytes as uint for modulo operation
            var selectionValue = BitConverter.ToUInt32(hashBytes, 0);
            var leadIndex = (int)(selectionValue % (uint)sortedArbiters.Count);

            return sortedArbiters[leadIndex];
        }

        /// <summary>
        /// Gets the list of active arbiters at the current time
        /// </summary>
        /// <returns>List of active arbiters</returns>
        public static List<Models.Arbiter> GetActiveArbiters()
        {
            var currentTime = TimeUtil.GetTime();
            return Globals.Arbiters
                .Where(x => x.EndOfService == null && x.StartOfService <= currentTime)
                .ToList();
        }

        /// <summary>
        /// Checks if the given address is the designated lead arbiter for a withdrawal request
        /// </summary>
        /// <param name="arbiterAddress">The arbiter address to check</param>
        /// <param name="uniqueId">The withdrawal unique ID</param>
        /// <param name="scUID">The smart contract UID</param>
        /// <returns>True if this arbiter is the lead arbiter, false otherwise</returns>
        public static bool IsLeadArbiter(string arbiterAddress, string uniqueId, string scUID)
        {
            var activeArbiters = GetActiveArbiters();
            var leadArbiter = SelectLeadArbiter(uniqueId, scUID, activeArbiters);
            
            return leadArbiter != null && leadArbiter.SigningAddress == arbiterAddress;
        }
    }
}
