using Microsoft.AspNetCore.Http.Connections;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The two validator hubs buffer 1 MB toward a peer that reads slowly (it was 8 MB a connection, and the server's
    /// memory pool does not shrink), and still take in 8 MB from a peer, so a large block is received as before.
    /// </summary>
    public class ValidatorHubBufferTests
    {
        [Fact]
        public void A_validator_hub_buffers_one_megabyte_out_and_eight_in()
        {
            var options = new HttpConnectionDispatcherOptions();
            StartupP2PValidator.ApplyHubBuffers(options);
            Assert.Equal(1024 * 1024, options.TransportMaxBufferSize);
            Assert.Equal(8 * 1024 * 1024, options.ApplicationMaxBufferSize);
        }
    }
}
