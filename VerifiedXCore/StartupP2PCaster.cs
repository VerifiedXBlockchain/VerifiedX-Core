using Microsoft.AspNetCore.Http.Connections;
using VerifiedXCore.Controllers;
using VerifiedXCore.Nodes;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;


namespace VerifiedXCore
{
    public class StartupP2PCaster
    {
        public static bool IsTestNet = false;
        public StartupP2PCaster(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {

            services.AddControllers(options => {
                options.EnableEndpointRouting = true;
            }).ConfigureApplicationPartManager(apm =>
            {
                var controllerFeatureProvider = new ExcludeControllersFeatureProvider<ValidatorController>();
                apm.FeatureProviders.Add(controllerFeatureProvider);
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

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                if (Globals.ValidatorAddress != null)
                {
                    // Map the Web API endpoint
                    endpoints.MapControllerRoute(
                        name: "validator_api",
                        pattern: "valapi/{controller=Validator}/{action=Index}/{id?}"
                    ).WithDisplayName("ValidatorAPI");
                }
            });
        }
    }
}
