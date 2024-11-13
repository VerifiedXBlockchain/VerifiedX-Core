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

            services.AddControllers(options => {
                options.EnableEndpointRouting = true;
            }).ConfigureApplicationPartManager(apm =>
            {
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
                options.MaximumParallelInvocationsPerClient = int.MaxValue;
                options.HandshakeTimeout = TimeSpan.FromSeconds(30);
            }).AddHubOptions<P2PValidatorServer>(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 8388608;
            });

            //Create hosted service for just consensus measures
            services.AddHostedService<ValidatorNode>();

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

                // Log incoming requests for debugging
                //Console.WriteLine($"Request path: {path}");
                //Console.WriteLine($"Request protocol: {context.Request.Protocol}");
                //Console.WriteLine($"Is WebSocket? {context.WebSockets.IsWebSocketRequest}");

                // Allow SignalR negotiation and WebSocket upgrade requests
                if (path?.StartsWith("/consensus") == true)
                {
                    await next();
                }
                else if (path?.StartsWith("/valapi") == true)
                {
                    await next();
                }
                else
                {
                    context.Response.StatusCode = 404;
                    return;
                }
            });

            //app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                if (Globals.ValidatorAddress != null)
                {
                    // Map the Web API endpoint
                    endpoints.MapControllerRoute(
                        name: "validator_api",
                        pattern: "valapi/{controller=Validator}/{action=Index}/{id?}"
                    ).WithDisplayName("ValidatorAPI");

                    // Map the SignalR hub with transport configuration
                    endpoints.MapHub<P2PValidatorServer>("/consensus", options =>
                    {
                        options.ApplicationMaxBufferSize = 8388608;
                        options.TransportMaxBufferSize = 8388608;
                        options.Transports = 
                            HttpTransportType.WebSockets | 
                            HttpTransportType.LongPolling;
                        options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(30);
                    }).WithDisplayName("ConsensusHub");
                }
            });
        }
    }
}
