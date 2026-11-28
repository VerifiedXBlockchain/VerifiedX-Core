using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The node runs the workstation collector, concurrent. The web SDK's default, the server collector, keeps a heap per
    /// core with a large budget and gives memory back slowly, so a validator held several GB after a few minutes of gossip.
    /// The setting lives in the runtime configuration the build writes next to the node, which is what the runtime reads.
    /// </summary>
    public class GarbageCollectorModeTests
    {
        [Fact]
        public void The_node_is_built_with_the_workstation_collector_running_concurrently()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "VerifiedXCore.runtimeconfig.json");
            Assert.True(File.Exists(path), $"the node's runtime configuration was not found at {path}");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var props = doc.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

            Assert.False(props.GetProperty("System.GC.Server").GetBoolean());
            Assert.True(props.GetProperty("System.GC.Concurrent").GetBoolean());
        }
    }
}
