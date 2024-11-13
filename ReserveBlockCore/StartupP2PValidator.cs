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
            services.AddControllers().ConfigureApplicationPartManager(apm =>
            {
                // Remove all controllers except the one you want (ValidatorController)
                var controllerFeatureProvider = new ExcludeControllersFeatureProvider<ValidatorController>();
                apm.FeatureProviders.Add(controllerFeatureProvider);
            });

            services.AddSignalR(options =>
            {
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
                options.MaximumReceiveMessageSize = 1179648;
                options.StreamBufferCapacity = 1024;
                options.EnableDetailedErrors = true;
                options.HandshakeTimeout = TimeSpan.FromSeconds(30);
                options.MaximumParallelInvocationsPerClient = int.MaxValue;
            }).AddHubOptions<P2PValidatorServer>(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 8388608;
            });

            //Create hosted service for just consensus measures
            services.AddHostedService<ValidatorNode>();

            // Add memory cache
            services.AddMemoryCache();

            // Add routing with strict constraints
            services.AddRouting(options =>
            {
                options.LowercaseUrls = true;
                options.LowercaseQueryStrings = true;
            });
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

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value?.ToLower();
                if (path?.StartsWith("/consensus") == true &&
                    context.WebSockets.IsWebSocketRequest)
                {
                    // Handle SignalR WebSocket connections
                    await next();
                }
                else if (path?.StartsWith("/valapi") == true)
                {
                    // Handle API requests
                    await next();
                }
                else
                {
                    context.Response.StatusCode = 404;
                    return;
                }
            });

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                if (Globals.ValidatorAddress != null)
                {
                    // Map validator controller with explicit route prefix
                    endpoints.MapControllerRoute(
                        name: "validator_controller",
                        pattern: "valapi/validatorcontroller/{action=Index}/{id?}",
                        defaults: new { controller = "Validator" }
                    ).WithDisplayName("ValidatorAPI");

                    // Map SignalR hub with specific options
                    endpoints.MapHub<P2PValidatorServer>("/consensus", options =>
                    {
                        options.ApplicationMaxBufferSize = 8388608;
                        options.TransportMaxBufferSize = 8388608;
                        options.Transports =
                            HttpTransportType.WebSockets |
                            HttpTransportType.ServerSentEvents;
                        options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(30);
                    }).WithDisplayName("ConsensusHub");
                }
            });
        }
    }
}
