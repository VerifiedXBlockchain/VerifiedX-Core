using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// S3C (Self-Sovereign Smart Contracts). Parses/validates a minting node's S3C= config and
    /// resolves the configured private validator pool for vBTC DKG / withdrawal ceremonies.
    /// See docs/PLAN-S3C.md (§2, §3.4, §4.2, §11).
    /// </summary>
    public static class S3CService
    {
        // FROST DKG minimum (mirrors VBTCThresholdCalculator.MINIMUM_VALIDATORS_ABSOLUTE).
        private const int MINIMUM_VALIDATORS = 3;

        /// <summary>
        /// Parse "IP:ValidatorAddress,IP:ValidatorAddress,..." into Globals.S3CPool. Enforces
        /// >= 3 entries, public-only IPs, and valid VFX addresses. On ANY failure: leaves
        /// S3CPool null, sets Globals.S3CConfigInvalid (mints refuse — no silent fallback to the
        /// public pool, §2.1), and returns false.
        /// </summary>
        public static bool ParseAndValidate(string raw)
        {
            Globals.S3CPool = null;
            Globals.S3CConfigInvalid = false;

            if (string.IsNullOrWhiteSpace(raw))
                return Fail("S3C config value is empty.");

            var entries = new List<S3CEntry>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawPart in parts)
            {
                var part = rawPart.Trim();
                // VFX addresses contain no ':'; IPv6 IPs do — split on the LAST colon.
                var idx = part.LastIndexOf(':');
                if (idx <= 0 || idx >= part.Length - 1)
                    return Fail($"entry malformed (expected IP:ValidatorAddress): '{part}'");

                var ip = part.Substring(0, idx).Trim();
                var addr = part.Substring(idx + 1).Trim();

                if (!InputValidationHelper.ValidateValidatorIPAddress(ip, out var ipErr))
                    return Fail($"entry IP '{ip}' is invalid (public IPs only): {ipErr}");
                if (!AddressValidateUtility.ValidateAddress(addr))
                    return Fail($"entry address '{addr}' is not a valid VFX address.");

                entries.Add(new S3CEntry { IPAddress = ip, ValidatorAddress = addr });
            }

            if (entries.Count < MINIMUM_VALIDATORS)
                return Fail($"requires at least {MINIMUM_VALIDATORS} validators; got {entries.Count}.");

            Globals.S3CPool = entries;
            return true;
        }

        private static bool Fail(string message)
        {
            ErrorLogUtility.LogError($"[S3C] {message}", "S3CService.ParseAndValidate()");
            Console.WriteLine($"[S3C] CONFIG ERROR: {message}");
            Globals.S3CPool = null;
            Globals.S3CConfigInvalid = true;
            return false;
        }

        /// <summary>
        /// Resolve the configured pool into ceremony participants. ALL-OR-NOTHING (§4.2): every
        /// entry MUST resolve to a registered, active validator with IsS3C==true (the §3.4
        /// transition precondition). Throws — naming the culprits — if any entry is unusable.
        /// Participants are returned with their CONFIGURED IP (authoritative — §4.2); identity
        /// (FrostPublicKey/BaseAddress) comes from the registry. The pool is never auto-discovered.
        /// </summary>
        public static List<VBTCValidator> GetValidatorsForCeremony()
        {
            var pool = Globals.S3CPool;
            if (pool == null || pool.Count < MINIMUM_VALIDATORS)
                throw new Exception("S3C pool is not configured (need >= 3 valid entries).");

            var result = new List<VBTCValidator>();
            var bad = new List<string>();
            foreach (var entry in pool)
            {
                var v = VBTCValidatorRegistry.GetValidator(entry.ValidatorAddress);
                if (v == null)
                    bad.Add($"{entry.ValidatorAddress} (not registered/heartbeating)");
                else if (!v.IsActive)
                    bad.Add($"{entry.ValidatorAddress} (inactive)");
                else if (!v.IsS3C)
                    bad.Add($"{entry.ValidatorAddress} (not flagged S3C on-chain)");
                else
                    result.Add(new VBTCValidator
                    {
                        ValidatorAddress = v.ValidatorAddress,
                        IPAddress = entry.IPAddress,            // §4.2: configured IP is authoritative
                        FrostPublicKey = v.FrostPublicKey,
                        BaseAddress = v.BaseAddress,
                        RegistrationBlockHeight = v.RegistrationBlockHeight,
                        LastHeartbeatBlock = v.LastHeartbeatBlock,
                        IsActive = true,
                        IsS3C = true
                    });
            }

            if (bad.Count > 0)
                throw new Exception($"S3C pool resolution failed (all-or-nothing). Unusable entries: {string.Join("; ", bad)}. " +
                    "Run GetS3CStatus and ensure every configured validator is registered, heartbeating, and S3C-flagged.");

            return result;
        }

        /// <summary>
        /// Pre-flight status for GetS3CStatus (§11): per-entry on-chain registration + S3C flag,
        /// FROST-port reachability, and config-vs-registry IP divergence (informational).
        /// </summary>
        public static async Task<List<S3CValidatorStatus>> ValidatePoolStatus()
        {
            var statuses = new List<S3CValidatorStatus>();
            var pool = Globals.S3CPool;
            if (pool == null) return statuses;

            // Probe at the CONFIGURED IPs (the addresses we would actually contact).
            var probeTargets = pool.Select(e => new VBTCValidator
            {
                ValidatorAddress = e.ValidatorAddress,
                IPAddress = e.IPAddress
            }).ToList();

            List<VBTCValidator> reachable;
            try { reachable = await FrostMPCService.ProbeValidatorReachability(probeTargets); }
            catch { reachable = new List<VBTCValidator>(); }
            var reachableSet = new HashSet<string>(reachable.Select(r => r.ValidatorAddress));

            foreach (var e in pool)
            {
                var v = VBTCValidatorRegistry.GetValidator(e.ValidatorAddress);
                statuses.Add(new S3CValidatorStatus
                {
                    IPAddress = e.IPAddress,
                    ValidatorAddress = e.ValidatorAddress,
                    RegisteredOnChain = v != null && v.IsActive && v.IsS3C,
                    Reachable = reachableSet.Contains(e.ValidatorAddress),
                    ConfigIPMismatch = v != null && !string.IsNullOrEmpty(v.IPAddress) && v.IPAddress != e.IPAddress
                });
            }
            return statuses;
        }
    }

    public class S3CValidatorStatus
    {
        public string IPAddress { get; set; }
        public string ValidatorAddress { get; set; }
        public bool RegisteredOnChain { get; set; }   // registered + active + IsS3C
        public bool Reachable { get; set; }            // FROST port health
        public bool ConfigIPMismatch { get; set; }     // config IP != on-chain IP (config wins; informational)
    }
}
