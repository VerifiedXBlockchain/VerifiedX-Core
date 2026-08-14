using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using System.Collections.Concurrent;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Tests for the add-only caster pool heal (CasterMembershipService.TryHealBlockCastersFromRecordAsync).
    /// Covers the steady-state gap where PingCasters evicts a committee member during a transient
    /// outage and nothing re-adds it once it recovers — observed live on testnet Aug 2026 where one
    /// caster ran for hours with a 2-of-3 pool, causing per-round winner disagreement.
    /// Bootstrap-mode gating is not unit-testable (Globals.IsBootstrapMode is computed from private
    /// BootstrapCoordinationService state); it shares PingCasters' call-site gate and is covered by
    /// the testnet drill.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class CasterHealTests
    {
        private static CasterMembershipRecord MakeRecord(long seq, long effective, string prevHash, string changeType, string? changedAddr, params (string Addr, string Ip, string Pk)[] casters)
        {
            var rec = new CasterMembershipRecord
            {
                RecordSeq = seq,
                EffectiveFromHeight = effective,
                PrevRecordHash = prevHash,
                ChangeType = changeType,
                ChangedAddress = changedAddr,
                Casters = casters.Select(c => new CasterInfo { Address = c.Addr, PeerIP = c.Ip, PublicKey = c.Pk }).ToList(),
                Signatures = new List<RecordSignature>()
            };
            rec.RecordHash = CasterMembershipStore.ComputeRecordHash(rec);
            return rec;
        }

        private static Peers MakePeer(string addr, string ip) => new Peers
        {
            ValidatorAddress = addr,
            PeerIP = ip,
            ValidatorPublicKey = $"pk-{addr}",
            IsValidator = true,
        };

        [Fact]
        public async Task Heal_ReAddsMissingReachableMember_AfterStreak()
        {
            var originalBag = Globals.BlockCasters;
            var originalValidators = Globals.NetworkValidators;
            var originalAddress = Globals.ValidatorAddress;
            List<CasterInfo> originalKnownCasters;
            lock (Globals.KnownCastersLock) { originalKnownCasters = Globals.KnownCasters.ToList(); }
            try
            {
                Globals.ValidatorAddress = "HealCasterA";
                Globals.NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>();
                Globals.BlockCasters = new ConcurrentBag<Peers>
                {
                    MakePeer("HealCasterA", "10.0.0.1"),
                    MakePeer("HealCasterB", "10.0.0.2"),
                };
                CasterMembershipService.ResetHealState();

                // Record stores C's IP in the ::ffff: form to prove normalization before probing.
                var record = MakeRecord(0, 100, "GENESIS", "Genesis", null,
                    ("HealCasterA", "10.0.0.1", "pkA"),
                    ("HealCasterB", "10.0.0.2", "pkB"),
                    ("HealCasterC", "::ffff:10.0.0.3", "pkC"));

                var probedIps = new List<string>();
                Task<bool> Probe(string ip) { probedIps.Add(ip); return Task.FromResult(true); }

                // Tick 1: reachable but streak not met — no add yet (hysteresis).
                var added1 = await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe);
                Assert.Equal(0, added1);
                Assert.DoesNotContain(Globals.BlockCasters, c => c.ValidatorAddress == "HealCasterC");

                // Tick 2: streak met — re-added with normalized IP and trust hydration.
                var added2 = await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe);
                Assert.Equal(1, added2);
                var healed = Globals.BlockCasters.FirstOrDefault(c => c.ValidatorAddress == "HealCasterC");
                Assert.NotNull(healed);
                Assert.Equal("10.0.0.3", healed!.PeerIP);
                Assert.All(probedIps, ip => Assert.Equal("10.0.0.3", ip));

                Assert.True(Globals.NetworkValidators.TryGetValue("HealCasterC", out var nv));
                Assert.True(nv!.IsFullyTrusted);
                Assert.Equal(0, nv.CheckFailCount);
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.NetworkValidators = originalValidators;
                Globals.ValidatorAddress = originalAddress;
                lock (Globals.KnownCastersLock)
                {
                    Globals.KnownCasters.Clear();
                    Globals.KnownCasters.AddRange(originalKnownCasters);
                }
                CasterMembershipService.ResetHealState();
            }
        }

        [Fact]
        public async Task Heal_SingleSuccess_DoesNotReAdd()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            try
            {
                Globals.ValidatorAddress = "HealCasterA";
                Globals.BlockCasters = new ConcurrentBag<Peers> { MakePeer("HealCasterA", "10.0.0.1") };
                CasterMembershipService.ResetHealState();

                var record = MakeRecord(0, 100, "GENESIS", "Genesis", null,
                    ("HealCasterA", "10.0.0.1", "pkA"),
                    ("HealCasterB", "10.0.0.2", "pkB"));

                var added = await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, _ => Task.FromResult(true));

                Assert.Equal(0, added);
                Assert.DoesNotContain(Globals.BlockCasters, c => c.ValidatorAddress == "HealCasterB");
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                CasterMembershipService.ResetHealState();
            }
        }

        [Fact]
        public async Task Heal_UnreachableResetsStreak()
        {
            var originalBag = Globals.BlockCasters;
            var originalValidators = Globals.NetworkValidators;
            var originalAddress = Globals.ValidatorAddress;
            List<CasterInfo> originalKnownCasters;
            lock (Globals.KnownCastersLock) { originalKnownCasters = Globals.KnownCasters.ToList(); }
            try
            {
                Globals.ValidatorAddress = "HealCasterA";
                Globals.NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>();
                Globals.BlockCasters = new ConcurrentBag<Peers> { MakePeer("HealCasterA", "10.0.0.1") };
                CasterMembershipService.ResetHealState();

                var record = MakeRecord(0, 100, "GENESIS", "Genesis", null,
                    ("HealCasterA", "10.0.0.1", "pkA"),
                    ("HealCasterB", "10.0.0.2", "pkB"));

                var probeResults = new Queue<bool>(new[] { true, false, true, true });
                Task<bool> Probe(string _) => Task.FromResult(probeResults.Dequeue());

                // true → streak 1; false → reset; true → streak 1; true → streak 2 → added.
                Assert.Equal(0, await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe));
                Assert.Equal(0, await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe));
                Assert.Equal(0, await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe));
                Assert.Equal(1, await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe));
                Assert.Contains(Globals.BlockCasters, c => c.ValidatorAddress == "HealCasterB");
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.NetworkValidators = originalValidators;
                Globals.ValidatorAddress = originalAddress;
                lock (Globals.KnownCastersLock)
                {
                    Globals.KnownCasters.Clear();
                    Globals.KnownCasters.AddRange(originalKnownCasters);
                }
                CasterMembershipService.ResetHealState();
            }
        }

        [Fact]
        public async Task Heal_NoOp_WhenBagComplete()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            try
            {
                Globals.ValidatorAddress = "HealCasterA";
                Globals.BlockCasters = new ConcurrentBag<Peers>
                {
                    MakePeer("HealCasterA", "10.0.0.1"),
                    MakePeer("HealCasterB", "10.0.0.2"),
                    MakePeer("HealCasterC", "10.0.0.3"),
                };
                CasterMembershipService.ResetHealState();
                var bagBefore = Globals.BlockCasters;

                var record = MakeRecord(0, 100, "GENESIS", "Genesis", null,
                    ("HealCasterA", "10.0.0.1", "pkA"),
                    ("HealCasterB", "10.0.0.2", "pkB"),
                    ("HealCasterC", "10.0.0.3", "pkC"));

                var probeCalls = 0;
                Task<bool> Probe(string _) { probeCalls++; return Task.FromResult(true); }

                var added = await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe);

                Assert.Equal(0, added);
                Assert.Equal(0, probeCalls);
                // Zero churn: the heal never replaces the bag instance, so no round-timing resets.
                Assert.True(ReferenceEquals(bagBefore, Globals.BlockCasters));
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                CasterMembershipService.ResetHealState();
            }
        }

        [Fact]
        public async Task Heal_NoOp_WhenRecordNull()
        {
            var originalBag = Globals.BlockCasters;
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers> { MakePeer("HealCasterA", "10.0.0.1") };
                CasterMembershipService.ResetHealState();

                var probeCalls = 0;
                Task<bool> Probe(string _) { probeCalls++; return Task.FromResult(true); }

                // Record store is dark in unit tests (no DB_Config) → GetCurrent() is null → legacy-era no-op.
                var added = await CasterMembershipService.TryHealBlockCastersFromRecordAsync(null, Probe);

                Assert.Equal(0, added);
                Assert.Equal(0, probeCalls);
                Assert.Single(Globals.BlockCasters);
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                CasterMembershipService.ResetHealState();
            }
        }

        [Fact]
        public async Task Heal_SkipsSelf()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            try
            {
                // Self is the missing member — eviction-awareness owns self re-admission, not the heal.
                Globals.ValidatorAddress = "HealCasterC";
                Globals.BlockCasters = new ConcurrentBag<Peers>
                {
                    MakePeer("HealCasterA", "10.0.0.1"),
                    MakePeer("HealCasterB", "10.0.0.2"),
                };
                CasterMembershipService.ResetHealState();

                var record = MakeRecord(0, 100, "GENESIS", "Genesis", null,
                    ("HealCasterA", "10.0.0.1", "pkA"),
                    ("HealCasterB", "10.0.0.2", "pkB"),
                    ("HealCasterC", "10.0.0.3", "pkC"));

                var probeCalls = 0;
                Task<bool> Probe(string _) { probeCalls++; return Task.FromResult(true); }

                var added = await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe);
                var added2 = await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe);

                Assert.Equal(0, added);
                Assert.Equal(0, added2);
                Assert.Equal(0, probeCalls);
                Assert.DoesNotContain(Globals.BlockCasters, c => c.ValidatorAddress == "HealCasterC");
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                CasterMembershipService.ResetHealState();
            }
        }

        [Fact]
        public async Task Heal_PoolFull_AddRejected()
        {
            var originalBag = Globals.BlockCasters;
            var originalAddress = Globals.ValidatorAddress;
            try
            {
                Globals.ValidatorAddress = "HealCasterA";
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                for (int i = 0; i < CasterDiscoveryService.MaxCasters; i++)
                    Globals.BlockCasters.Add(MakePeer($"FullPoolAddr{i}", $"10.0.1.{i}"));
                CasterMembershipService.ResetHealState();

                var record = MakeRecord(0, 100, "GENESIS", "Genesis", null,
                    ("HealCasterD", "10.0.0.4", "pkD"));

                Task<bool> Probe(string _) => Task.FromResult(true);

                // Two ticks so the streak is met and the add is actually attempted (and rejected).
                Assert.Equal(0, await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe));
                Assert.Equal(0, await CasterMembershipService.TryHealBlockCastersFromRecordAsync(record, Probe));

                Assert.Equal(CasterDiscoveryService.MaxCasters, Globals.BlockCasters.Count);
                Assert.DoesNotContain(Globals.BlockCasters, c => c.ValidatorAddress == "HealCasterD");
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.ValidatorAddress = originalAddress;
                CasterMembershipService.ResetHealState();
            }
        }
    }
}
