using Microsoft.AspNetCore.Http.Features;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using System.Diagnostics;
using System.Net;

namespace VerifiedXCore.Beacon
{
    public class BeaconStartup
    {
        public BeaconStartup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }
        public static string SaveArea =  GetPathUtility.GetBeaconPath();
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
            services.Configure<FormOptions>(x =>
            {
                x.ValueLengthLimit = 152 * 1024 * 1024; // 150 MB
                x.MultipartBodyLengthLimit = 152 * 1024 * 1024; // 150 MB
            });
        }
        public void Configure(IApplicationBuilder app)
        {
            // Configure the request pipeline
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/data", async context =>
                {
                    // Handle the GET request
                    var ipAddress = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
                    await context.Response.WriteAsync($"Hello {ipAddress}, this is the server's response!");
                });

                endpoints.MapPost("/upload/{scUID}", async context =>
                {
                    // Increase the maximum request body size
                    try
                    {
                        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (bodySize != null && !bodySize.IsReadOnly) bodySize.MaxRequestBodySize = 152 * 1024 * 1024; // 150 MB (absent outside Kestrel, e.g. TestServer)
                        var scUID = context.Request.RouteValues["scUID"] as string;
                        var ipAddress = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
                        // Check if the request contains a file
                        if (context.Request.Form.Files.Count > 0)
                        {
                            var file = context.Request.Form.Files[0];

                            // Save the uploaded file
                            var fileName = file.FileName;
                            // NEW-03: the multipart file name and the route UID were concatenated onto the beacon folder
                            // unchecked ("../../x" wrote anywhere). Resolve inside the contract's folder or refuse.
                            if (!BeaconPaths.TryResolve(SaveArea, scUID, fileName, out var filePath))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync("Invalid file name.");
                                return;
                            }

                            var extChkResult = CheckExtension(fileName);
                            if (!extChkResult)
                            {
                                //Extension found in reject list
                                context.Response.StatusCode = StatusCodes.Status403Forbidden; // Bad Request
                                await context.Response.WriteAsync("No file was uploaded. Extension was found in auto reject list.");
                                return;
                            }

                            bool fileExist = File.Exists(filePath);
                            if (fileExist)
                            {
                                context.Response.StatusCode = StatusCodes.Status202Accepted;
                                //await context.Response.WriteAsync("No file was uploaded. The file already exist");
                                return;
                            }

                            var contractFolder = Path.GetDirectoryName(filePath)!;
                            if (!Directory.Exists(contractFolder))
                                Directory.CreateDirectory(contractFolder);


                            var beaconData = BeaconData.GetBeaconData();
                            if (beaconData != null)
                            {
                                // NEW-03 (follow-up): the registration must be for THIS contract (the route's UID); an asset
                                // registered under one's own contract could be planted in another contract's folder.
                                var authCheck = beaconData.Exists(x => x.IPAdress == ipAddress && x.AssetName == fileName && x.SmartContractUID == scUID);
                                if (!authCheck)
                                {
                                    context.Response.StatusCode = StatusCodes.Status403Forbidden; // Bad Request
                                    await context.Response.WriteAsync("No file was uploaded. Extension was found in auto reject list.");
                                    return;
                                }
                                else
                                {
                                    var _beaconData = beaconData.Where(x => x.IPAdress == ipAddress && x.AssetName == fileName && x.SmartContractUID == scUID).FirstOrDefault();
                                    if (_beaconData != null)
                                    {
                                        using (var stream = new FileStream(filePath, FileMode.Create))
                                        {
                                            await file.CopyToAsync(stream);
                                        }

                                        await context.Response.WriteAsync($"File uploaded successfully!");

                                        _beaconData.AssetReceiveDate = TimeUtil.GetTime();//received today
                                        _beaconData.AssetExpireDate = TimeUtil.GetTimeForBeaconRelease(); //expires in 5 days
                                        var beaconDatas = BeaconData.GetBeacon();
                                        if (beaconDatas != null)
                                        {
                                            beaconDatas.UpdateSafe(_beaconData);
                                        }

                                        return;
                                    }

                                }
                            }
                            else
                            {
                                Console.WriteLine("Warning: Beacon Data was Null!");
                                context.Response.StatusCode = StatusCodes.Status409Conflict;
                                return;
                            }


                        }
                        else
                        {
                            context.Response.StatusCode = 400; // Bad Request
                            await context.Response.WriteAsync("No file was uploaded.");
                        }
                    }
                    catch(Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.ToString()}");
                    }
                    
                });

                endpoints.MapGet("/download/{scUID}/{fileName}", async context =>
                {
                    try
                    {
                        if (Globals.OptionalLogging)
                            Console.WriteLine("Optional Logging Enabled.");

                        var scUID = context.Request.RouteValues["scUID"] as string;
                        var fileName = context.Request.RouteValues["fileName"] as string;
                        var ipAddress = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();

                        if (Globals.OptionalLogging)
                        {
                            Console.WriteLine($"Starting download for NFT: {scUID}");
                            Console.WriteLine($"Filename: {fileName}");
                            Console.WriteLine($"IpAddress: {ipAddress}");
                        }

                        if (string.IsNullOrEmpty(fileName))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            if (Globals.OptionalLogging)
                                Console.WriteLine("Filename was missing.");
                            await context.Response.WriteAsync("Filename was missing.");
                            return;
                        }

                        // NEW-03: resolve inside the contract's folder or refuse (a download read any file on Windows).
                        if (!BeaconPaths.TryResolve(SaveArea, scUID, fileName, out var filePath))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync("Invalid file name.");
                            return;
                        }
                        bool fileExist = File.Exists(filePath);
                        if (!fileExist)
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            if (Globals.OptionalLogging)
                                Console.WriteLine("File was not found.");
                            await context.Response.WriteAsync("File was not found.");
                            return;
                        }

                        var beaconDataDb = BeaconData.GetBeacon();
                        if (beaconDataDb != null)
                        {
                            var bdd = beaconDataDb.FindOne(x => x.AssetName.ToLower() == fileName.ToLower() && x.DownloadIPAddress == ipAddress && x.SmartContractUID == scUID);
                            if (bdd != null)
                            {
                                context.Response.ContentType = "application/octet-stream";
                                context.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");

                                await using var stream = new FileStream(filePath, FileMode.Open);
                                await stream.CopyToAsync(context.Response.Body);
                            }
                            else
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                if (Globals.OptionalLogging)
                                    Console.WriteLine("bdd was null!");
                                await context.Response.WriteAsync("bdd was null!");
                                return;
                            }
                        }
                        else
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            if (Globals.OptionalLogging)
                                Console.WriteLine("Beacon data was null. Insure beacon is synced.");
                            await context.Response.WriteAsync("Beacon data was null.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Globals.OptionalLogging)
                            Console.WriteLine($"Error: {ex}");
                        context.Response.StatusCode = StatusCodes.Status417ExpectationFailed;
                        return;
                    }
                    
                });
            });
        }

        private static bool CheckExtension(string fileName)
        {
            return BeaconPaths.ExtensionAllowed(fileName); // NEW-03: case-insensitive
        }
    }
}
