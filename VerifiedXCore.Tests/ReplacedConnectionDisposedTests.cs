using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// When a peer is dialled again its node record takes the new connection. The one it replaces is disposed: it was
    /// left as it was, with its socket, buffers and receive loop, until the peer closed it.
    /// </summary>
    public class ReplacedConnectionDisposedTests
    {
        private static HubConnection Unstarted() => new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/none").Build();

        [Fact]
        public async Task The_connection_a_node_record_replaces_is_disposed_and_the_new_one_is_not()
        {
            var first = Unstarted();
            var second = Unstarted();
            var node = new NodeInfo { Connection = first };

            await node.ReplaceConnection(second);
            Assert.Same(second, node.Connection);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => first.StartAsync());   // disposed

            // The same connection put back (a first dial adds the record with it) is left alone.
            await node.ReplaceConnection(second);
            Assert.Same(second, node.Connection);
            var ex = await Record.ExceptionAsync(() => second.StartAsync());                 // refused by the port, not disposed
            Assert.IsNotType<ObjectDisposedException>(ex);
            await second.DisposeAsync();

            // A record with no connection yet just takes one.
            var empty = new NodeInfo();
            var third = Unstarted();
            await empty.ReplaceConnection(third);
            Assert.Same(third, empty.Connection);
            await NodeInfo.DisposeQuietly(third);
            await NodeInfo.DisposeQuietly(null);
        }
    }
}
