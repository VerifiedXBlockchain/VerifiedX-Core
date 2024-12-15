﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReserveBlockCore
{
    public class StartupP2P
    {
        public static bool IsTestNet = false;
        public StartupP2P(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            //services.AddControllers();
            services.AddSignalR(options => {
                options.KeepAliveInterval = TimeSpan.FromSeconds(15); //check connections everyone 15 seconds
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(60); //close connection after 60 seconds
                options.MaximumReceiveMessageSize = 1179648;
                options.StreamBufferCapacity = 25; //was 1024
                options.EnableDetailedErrors = true;
                options.MaximumParallelInvocationsPerClient = int.MaxValue;
            });
            services.AddHostedService<ClientCallService>();
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
                //endpoints.MapControllers();

                //change to mother local DB rec
                if(Mother.GetMother() != null)
                {
                    endpoints.MapHub<P2PMotherServer>("/mother", options => {
                        options.ApplicationMaxBufferSize = 1500000; // values might need tweaking if mem consumption gets too large
                        options.TransportMaxBufferSize = 1500000; // values might need tweaking if mem consumption gets too large
                    });
                }

                if(Globals.AdjudicateAccount == null)
                {
                    endpoints.MapHub<P2PServer>("/blockchain", options => {
                        options.ApplicationMaxBufferSize = 1500000; // values might need tweaking if mem consumption gets too large
                        options.TransportMaxBufferSize = 1500000; // values might need tweaking if mem consumption gets too large                    
                    });
                    endpoints.MapHub<P2PBeaconServer>("/beacon", options => {
                        options.ApplicationMaxBufferSize = 1500000; // values might need tweaking if mem consumption gets too large
                        options.TransportMaxBufferSize = 1500000; // values might need tweaking if mem consumption gets too large
                    });
                }
                
                if(Globals.AdjudicateAccount != null)
                {
                    endpoints.MapHub<P2PAdjServer>("/adjudicator", options => {
                        options.ApplicationMaxBufferSize = 8388608; // values might need tweaking if mem consumption gets too large
                        options.TransportMaxBufferSize = 8388608; // values might need tweaking if mem consumption gets too large
                    });
                    //endpoints.MapHub<ConsensusServer>("/consensus", options => {
                    //    options.ApplicationMaxBufferSize = 8388608; // values might need tweaking if mem consumption gets too large
                    //    options.TransportMaxBufferSize = 8388608; // values might need tweaking if mem consumption gets too large
                    //});
                }
                
                
            });
        }
    }
}
