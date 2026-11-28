using System;
using System.Collections.Concurrent;
using System.Reflection;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The mint progress a caster keeps in memory for the progress route is bounded: a lock's state is dropped half an
    /// hour after it was last collected, and no more than a fixed number of locks are held at once.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MintAttestationStateBoundsTests : IDisposable
    {
        public void Dispose() => AttestationStates.Clear();

        private static ConcurrentDictionary<string, MintAttestationState> AttestationStates =>
            (ConcurrentDictionary<string, MintAttestationState>)typeof(BaseBridgeAttestationService)
                .GetField("Pending", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        [Fact]
        public void CollectedProgress_IsDroppedWhenNoLongerCollected_AndNeverHeldPastTheLimit()
        {
            var now = TimeUtil.GetTime();
            AttestationStates.Clear();
            AttestationStates["collected-long-ago"] = new MintAttestationState { LockId = "collected-long-ago", CreatedAt = now - BaseBridgeAttestationService.AttestationStateSeconds - 1, Status = "Ready" };
            AttestationStates["still-collected"] = new MintAttestationState { LockId = "still-collected", CreatedAt = now - 30 };

            Assert.Equal(1, BaseBridgeAttestationService.PruneAttestationStates(now));
            Assert.Null(BaseBridgeAttestationService.GetAttestationState("collected-long-ago"));
            Assert.NotNull(BaseBridgeAttestationService.GetAttestationState("still-collected"));

            // Many locks at once: the oldest go first, down to the limit.
            for (var i = 0; i < BaseBridgeAttestationService.MaxAttestationStates + 500; i++)
                AttestationStates[$"lock-{i}"] = new MintAttestationState { LockId = $"lock-{i}", CreatedAt = now - 20 + (i % 10) };
            BaseBridgeAttestationService.PruneAttestationStates(now);
            Assert.Equal(BaseBridgeAttestationService.MaxAttestationStates, BaseBridgeAttestationService.AttestationStatesHeld);
        }

        [Fact]
        public void ALongBacklogCollectedInOneRound_NeverHoldsMoreThanTheLimit()
        {
            // As the collector holds each lock's state while it works through more pending locks than the limit.
            var now = TimeUtil.GetTime();
            AttestationStates.Clear();
            for (var i = 0; i < BaseBridgeAttestationService.MaxAttestationStates + 300; i++)
            {
                BaseBridgeAttestationService.Keep(new MintAttestationState { LockId = $"backlog-{i}", CreatedAt = now + i });
                Assert.True(BaseBridgeAttestationService.AttestationStatesHeld <= BaseBridgeAttestationService.MaxAttestationStates);
            }
            // The newest are the ones kept.
            Assert.NotNull(BaseBridgeAttestationService.GetAttestationState($"backlog-{BaseBridgeAttestationService.MaxAttestationStates + 299}"));
            Assert.Null(BaseBridgeAttestationService.GetAttestationState("backlog-0"));
        }
    }
}
