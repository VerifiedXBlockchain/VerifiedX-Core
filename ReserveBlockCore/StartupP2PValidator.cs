using Microsoft.AspNetCore.Http.Connections;
using ReserveBlockCore.Controllers;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;


namespace ReserveBlockCore
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
                options.EnableDetailedErrors = true;
                options.MaximumParallelInvocationsPerClient = 20; // HAL-054 Fix: Limit concurrent invocations per client
                options.HandshakeTimeout = TimeSpan.FromSeconds(30);
            }).AddHubOptions<P2PValidatorServer>(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 8388608;
            })
            .AddHubOptions<P2PBlockcasterServer>(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 8388608;
            });

            //Create hosted service for just consensus measures
            services.AddHostedService<ValidatorNode>();
            services.AddHostedService<BlockcasterNode>();

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            //app.UseHttpsRedirection();

            app.UseRouting();

            //app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                if (Globals.ValidatorAddress != null)
                {
                    // Map the SignalR hub with transport configuration
                    endpoints.MapHub<P2PValidatorServer>("/consensus", options =>
                    {
                        options.ApplicationMaxBufferSize = 8388608;
                        options.TransportMaxBufferSize = 8388608;
                    }).WithDisplayName("ConsensusHub");

                    endpoints.MapHub<P2PBlockcasterServer>("/blockcaster", options =>
                    {
                        options.ApplicationMaxBufferSize = 8388608;
                        options.TransportMaxBufferSize = 8388608;
                    }).WithDisplayName("BlockcasterHub");
                }
            });
        }
    }
}
