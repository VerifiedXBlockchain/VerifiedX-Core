using Microsoft.AspNetCore.Http.Connections;
using VerifiedXCore.Controllers;
using VerifiedXCore.Nodes;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;


namespace VerifiedXCore
{
    public class StartupP2PValidator
    {
        public static bool IsTestNet = false;
        public StartupP2PValidator(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Add memory cache
            services.AddMemoryCache();

            services.AddSignalR(options =>
            {
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
                options.MaximumReceiveMessageSize = 1179648;
                options.StreamBufferCapacity = 1024;
                options.EnableDetailedErrors = false; // VX-23 (follow-up): no exception detail to remote peers
                options.MaximumParallelInvocationsPerClient = 20; // HAL-054 Fix: Limit concurrent invocations per client
                options.HandshakeTimeout = TimeSpan.FromSeconds(30);
            }).AddHubOptions<P2PValidatorServer>(options =>
            {
                options.EnableDetailedErrors = false; // VX-23 (follow-up): no exception detail to remote peers
                options.MaximumReceiveMessageSize = 8388608;
            })
            .AddHubOptions<P2PBlockcasterServer>(options =>
            {
                options.EnableDetailedErrors = false; // VX-23 (follow-up): no exception detail to remote peers
                options.MaximumReceiveMessageSize = 8388608;
            });

            //Create hosted service for just consensus measures
            services.AddHostedService<ValidatorNode>();
            services.AddHostedService<BlockcasterNode>();

        }

        /// <summary>What a validator hub buffers of what a peer sends before it reads on: a whole block fits.</summary>
        internal const long HubReceiveBufferBytes = 8 * 1024 * 1024;
        /// <summary>
        /// What a validator hub buffers toward a peer that reads slowly. A message larger than this is still sent; the next
        /// one waits. At 8 MB a connection, with the server's memory pool never shrinking, a few slow peers held tens of MB.
        /// </summary>
        internal const long HubSendBufferBytes = 1024 * 1024;

        /// <summary>The buffers of the two validator hubs, /consensus and /blockcaster.</summary>
        internal static void ApplyHubBuffers(Microsoft.AspNetCore.Http.Connections.HttpConnectionDispatcherOptions options)
        {
            options.ApplicationMaxBufferSize = HubReceiveBufferBytes;
            options.TransportMaxBufferSize = HubSendBufferBytes;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // VX-23 (adjacent): network-facing P2P host — an unhandled exception returns 500 with no body, never the
            // developer exception page (it was enabled whenever ASPNETCORE_ENVIRONMENT=Development).
            app.UseExceptionHandler(errorApp => errorApp.Run(context =>
            {
                context.Response.StatusCode = 500;
                return Task.CompletedTask;
            }));
            app.UseQuietCutOffBodies();

            //app.UseHttpsRedirection();

            app.UseRouting();

            //app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                if (Globals.ValidatorAddress != null)
                {
                    // Map the SignalR hub with transport configuration
                    endpoints.MapHub<P2PValidatorServer>("/consensus", ApplyHubBuffers).WithDisplayName("ConsensusHub");

                    endpoints.MapHub<P2PBlockcasterServer>("/blockcaster", ApplyHubBuffers).WithDisplayName("BlockcasterHub");
                }
            });
        }
    }
}
