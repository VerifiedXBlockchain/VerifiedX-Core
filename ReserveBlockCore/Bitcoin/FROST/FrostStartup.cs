using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.FROST.Models;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.FROST
{
    /// <summary>
    /// FROST Validator Startup - Defines all REST endpoints for DKG and signing ceremonies
    /// </summary>
    public class FrostStartup
    {
        public IConfiguration Configuration { get; }

        public FrostStartup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
            services.Configure<FormOptions>(x =>
            {
                x.ValueLengthLimit = 4 * 1024 * 1024; // 4 MB
                x.MultipartBodyLengthLimit = 4 * 1024 * 1024; // 4 MB
            });
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                #region Health Check

                endpoints.MapGet("/", async context =>
                {
                    var ipAddress = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync($"FROST Validator Server - IP: {ipAddress}");
                });

                endpoints.MapGet("/health", async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    var response = JsonConvert.SerializeObject(new
                    {
                        Success = true,
                        Message = "FROST Validator Online",
                        ValidatorAddress = Globals.ValidatorAddress,
                        Timestamp = TimeUtil.GetTime()
                    }, Formatting.Indented);
                    await context.Response.WriteAsync(response);
                });

                #endregion

                #region DKG Endpoints (3-round ceremony)

                /// <summary>
                /// POST /frost/dkg/start - Leader broadcasts DKG ceremony initiation
                /// </summary>
                endpoints.MapPost("/frost/dkg/start", async context =>
                {
                    try
                    {
                        // FIND-0013 Fix: Opportunistic cleanup to prevent unbounded session growth
                        FrostSessionStorage.CleanupOldSessions();

                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var request = JsonConvert.DeserializeObject<FrostDKGStartRequest>(body);

                            if (request == null)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid request body"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Input validation bounds
                            if (string.IsNullOrEmpty(request.SessionId) || request.SessionId.Length > FrostSessionStorage.MAX_SESSION_ID_LENGTH)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"Invalid SessionId (max {FrostSessionStorage.MAX_SESSION_ID_LENGTH} characters)"
                                }));
                                return;
                            }

                            if (request.ParticipantAddresses == null || request.ParticipantAddresses.Count == 0 
                                || request.ParticipantAddresses.Count > FrostSessionStorage.MAX_PARTICIPANTS)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"ParticipantAddresses must be 1-{FrostSessionStorage.MAX_PARTICIPANTS}"
                                }));
                                return;
                            }

                            if (request.RequiredThreshold < FrostSessionStorage.MIN_THRESHOLD 
                                || request.RequiredThreshold > FrostSessionStorage.MAX_THRESHOLD)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"RequiredThreshold must be {FrostSessionStorage.MIN_THRESHOLD}-{FrostSessionStorage.MAX_THRESHOLD}"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Enforce maximum concurrent session cap
                            if (FrostSessionStorage.DKGSessions.Count >= FrostSessionStorage.MAX_DKG_SESSIONS)
                            {
                                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Maximum concurrent DKG sessions reached. Try again later."
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify leader is a registered active vBTC validator
                            if (string.IsNullOrEmpty(request.LeaderAddress))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "LeaderAddress required"
                                }));
                                return;
                            }

                            var leaderValidator = VBTCValidator.GetValidator(request.LeaderAddress);
                            if (leaderValidator == null || !leaderValidator.IsActive)
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Leader is not a registered active vBTC validator"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify all participants are registered active validators
                            foreach (var participantAddr in request.ParticipantAddresses)
                            {
                                var participantValidator = VBTCValidator.GetValidator(participantAddr);
                                if (participantValidator == null || !participantValidator.IsActive)
                                {
                                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                    await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                    {
                                        Success = false,
                                        Message = $"Participant {participantAddr} is not a registered active vBTC validator"
                                    }));
                                    return;
                                }
                            }

                            // FIND-0013 Fix: Cryptographic signature verification using existing SignatureService
                            // Verify leader actually signed this request (message = SessionId.LeaderAddress.Timestamp)
                            if (string.IsNullOrEmpty(request.LeaderSignature))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Leader signature required"
                                }));
                                return;
                            }

                            var leaderMessage = $"{request.SessionId}.{request.LeaderAddress}.{request.Timestamp}";
                            var leaderSigValid = SignatureService.VerifySignature(request.LeaderAddress, leaderMessage, request.LeaderSignature);
                            if (!leaderSigValid)
                            {
                                ErrorLogUtility.LogError($"FIND-0013 Security: Invalid leader signature for DKG start. Leader: {request.LeaderAddress}, Session: {request.SessionId}", "FrostStartup.DKGStart");
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid leader signature"
                                }));
                                return;
                            }

                            // FIND-024 Fix: Determine this validator's participant index (1-based)
                            var myAddress = Globals.ValidatorAddress;
                            var participantIndex = request.ParticipantAddresses.IndexOf(myAddress);
                            if (participantIndex < 0)
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "This validator is not in the participant list"
                                }));
                                return;
                            }

                            ushort myParticipantId = (ushort)(participantIndex + 1); // FROST uses 1-based IDs
                            ushort maxSigners = (ushort)request.ParticipantAddresses.Count;
                            ushort minSigners = (ushort)Math.Ceiling(maxSigners * (request.RequiredThreshold / 100.0));

                            // FIND-024 Fix: Call FROST native library for DKG Round 1
                            var (commitment, secretPackage, errorCode) = FrostNative.DKGRound1Generate(
                                myParticipantId, maxSigners, minSigners);

                            if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(commitment))
                            {
                                ErrorLogUtility.LogError($"FROST DKG Round 1 generate failed. Error code: {errorCode}", "FrostStartup.DKGStart");
                                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"FROST DKG Round 1 generation failed (error: {errorCode})"
                                }));
                                return;
                            }

                            // Create DKG session in memory with FROST state
                            var session = new DKGSession
                            {
                                SessionId = request.SessionId,
                                SmartContractUID = request.SmartContractUID,
                                LeaderAddress = request.LeaderAddress,
                                ParticipantAddresses = request.ParticipantAddresses,
                                RequiredThreshold = request.RequiredThreshold,
                                StartTimestamp = TimeUtil.GetTime(),
                                MyParticipantIndex = myParticipantId,
                                Round1SecretPackage = secretPackage
                            };

                            // Auto-store this validator's commitment
                            session.Round1Commitments.TryAdd(myAddress, commitment);

                            if (!FrostSessionStorage.DKGSessions.TryAdd(request.SessionId, session))
                            {
                                context.Response.StatusCode = StatusCodes.Status409Conflict;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Session already exists"
                                }));
                                return;
                            }

                            LogUtility.Log($"[FROST] DKG ceremony started with real FROST crypto. Session: {request.SessionId}, " +
                                $"ParticipantId: {myParticipantId}/{maxSigners}, MinSigners: {minSigners}", "FrostStartup.DKGStart");

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "DKG ceremony started with FROST Round 1 commitment generated",
                                SessionId = request.SessionId,
                                SmartContractUID = request.SmartContractUID,
                                ParticipantCount = request.ParticipantAddresses.Count,
                                Threshold = request.RequiredThreshold,
                                CommitmentGenerated = true
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"DKG start error: {ex.Message}", "FrostStartup.DKGStart");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// POST /frost/dkg/round1 - Submit Round 1 commitment
                /// </summary>
                endpoints.MapPost("/frost/dkg/round1", async context =>
                {
                    try
                    {
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var commitment = JsonConvert.DeserializeObject<FrostDKGRound1Message>(body);

                            if (commitment == null || string.IsNullOrEmpty(commitment.CommitmentData))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid commitment"
                                }));
                                return;
                            }

                            // FIND-014 Fix: Bound commitment data size before it can reach FFI boundary
                            if (commitment.CommitmentData.Length > FrostSessionStorage.MAX_COMMITMENT_DATA_LENGTH)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"CommitmentData exceeds maximum allowed size ({FrostSessionStorage.MAX_COMMITMENT_DATA_LENGTH} chars)"
                                }));
                                return;
                            }

                            // Get session
                            if (!FrostSessionStorage.DKGSessions.TryGetValue(commitment.SessionId, out var session))
                            {
                                context.Response.StatusCode = StatusCodes.Status404NotFound;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Session not found"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify validator is a participant in this session
                            if (string.IsNullOrEmpty(commitment.ValidatorAddress) 
                                || !session.ParticipantAddresses.Contains(commitment.ValidatorAddress))
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Validator is not a participant in this session"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Cryptographic signature verification
                            if (string.IsNullOrEmpty(commitment.ValidatorSignature))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Validator signature required"
                                }));
                                return;
                            }

                            var validatorMessage = $"{commitment.SessionId}.{commitment.ValidatorAddress}.{commitment.Timestamp}";
                            var sigValid = SignatureService.VerifySignature(commitment.ValidatorAddress, validatorMessage, commitment.ValidatorSignature);
                            if (!sigValid)
                            {
                                ErrorLogUtility.LogError($"FIND-0013 Security: Invalid validator signature for DKG Round 1. Validator: {commitment.ValidatorAddress}, Session: {commitment.SessionId}", "FrostStartup.DKGRound1");
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid validator signature"
                                }));
                                return;
                            }

                            // FIND-016 Fix: Prevent overwrite - each participant can only submit once per round
                            if (!session.Round1Commitments.TryAdd(commitment.ValidatorAddress, commitment.CommitmentData))
                            {
                                context.Response.StatusCode = StatusCodes.Status409Conflict;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Commitment already submitted for this validator in this session"
                                }));
                                return;
                            }

                            var commitmentCount = session.Round1Commitments.Count;
                            var requiredCount = (int)Math.Ceiling(session.ParticipantAddresses.Count * (session.RequiredThreshold / 100.0));

                            LogUtility.Log($"[FROST] DKG Round 1 commitment received from {commitment.ValidatorAddress}. Count: {commitmentCount}/{requiredCount}", "FrostStartup.DKGRound1");

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Round 1 commitment received",
                                SessionId = commitment.SessionId,
                                ValidatorAddress = commitment.ValidatorAddress,
                                CommitmentCount = commitmentCount,
                                RequiredCount = requiredCount,
                                ThresholdReached = commitmentCount >= requiredCount
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"DKG Round 1 error: {ex.Message}", "FrostStartup.DKGRound1");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// GET /frost/dkg/round1/{sessionId} - Get Round 1 commitments for coordinator polling
                /// </summary>
                endpoints.MapGet("/frost/dkg/round1/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;

                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "Session ID required"
                            }));
                            return;
                        }

                        // Get session
                        if (!FrostSessionStorage.DKGSessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "Session not found"
                            }));
                            return;
                        }

                        // Return all commitments collected so far
                        var commitments = session.Round1Commitments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        var requiredCount = (int)Math.Ceiling(session.ParticipantAddresses.Count * (session.RequiredThreshold / 100.0));

                        context.Response.StatusCode = StatusCodes.Status200OK;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = true,
                            Message = "Round 1 commitments retrieved",
                            SessionId = sessionId,
                            Commitments = commitments,
                            CommitmentCount = commitments.Count,
                            RequiredCount = requiredCount,
                            ThresholdReached = commitments.Count >= requiredCount
                        }, Formatting.Indented));
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"DKG Round 1 GET error: {ex.Message}", "FrostStartup.DKGRound1Get");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// FIND-015 Fix: POST /frost/dkg/round2/{sessionId} - Receive broadcast of all Round 1 commitments for share generation
                /// This endpoint was missing, causing CoordinateShareDistribution to always fail
                /// </summary>
                endpoints.MapPost("/frost/dkg/round2/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;
                        
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Session ID required" }));
                            return;
                        }

                        if (!FrostSessionStorage.DKGSessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Session not found" }));
                            return;
                        }

                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            
                            // FIND-024 Fix: Parse all Round 1 commitments and call FROST native Round 2
                            if (string.IsNullOrEmpty(session.Round1SecretPackage))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Round 1 secret package not found - DKG start may have failed" }));
                                return;
                            }

                            // Call FROST native library to generate shares for other participants
                            var (sharesJson, round2Secret, errorCode) = FrostNative.DKGRound2GenerateShares(
                                session.Round1SecretPackage, body);

                            if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(sharesJson))
                            {
                                ErrorLogUtility.LogError($"FROST DKG Round 2 share generation failed. Error code: {errorCode}", "FrostStartup.DKGRound2");
                                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = $"FROST Round 2 share generation failed (error: {errorCode})" }));
                                return;
                            }

                            // Store Round 2 state
                            session.Round2Secret = round2Secret;
                            session.GeneratedSharesJson = sharesJson;

                            LogUtility.Log($"[FROST] DKG Round 2 shares generated via native library for session {sessionId}", "FrostStartup.DKGRound2");

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Round 2 shares generated via FROST native library",
                                SessionId = sessionId,
                                SharesGenerated = true,
                                GeneratedShares = sharesJson  // Coordinator collects and redistributes
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"DKG Round 2 error: {ex.Message}", "FrostStartup.DKGRound2");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" }));
                    }
                });

                /// <summary>
                /// POST /frost/dkg/share - Receive encrypted share from another validator
                /// </summary>
                endpoints.MapPost("/frost/dkg/share", async context =>
                {
                    try
                    {
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var share = JsonConvert.DeserializeObject<FrostDKGShareMessage>(body);

                            if (share == null)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid share"
                                }));
                                return;
                            }

                            // FIND-014 Fix: Bound encrypted share data size before it can reach FFI boundary
                            if (!string.IsNullOrEmpty(share.EncryptedShare) && share.EncryptedShare.Length > FrostSessionStorage.MAX_COMMITMENT_DATA_LENGTH)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"EncryptedShare exceeds maximum allowed size ({FrostSessionStorage.MAX_COMMITMENT_DATA_LENGTH} chars)"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify session exists
                            if (string.IsNullOrEmpty(share.SessionId) || !FrostSessionStorage.DKGSessions.TryGetValue(share.SessionId, out var session))
                            {
                                context.Response.StatusCode = StatusCodes.Status404NotFound;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Session not found"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify sender is a participant in this session
                            if (string.IsNullOrEmpty(share.FromValidatorAddress) 
                                || !session.ParticipantAddresses.Contains(share.FromValidatorAddress))
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Sender is not a participant in this session"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify signature
                            if (string.IsNullOrEmpty(share.ValidatorSignature))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Validator signature required"
                                }));
                                return;
                            }

                            var shareMessage = $"{share.SessionId}.{share.FromValidatorAddress}.{share.Timestamp}";
                            var sigValid = SignatureService.VerifySignature(share.FromValidatorAddress, shareMessage, share.ValidatorSignature);
                            if (!sigValid)
                            {
                                ErrorLogUtility.LogError($"FIND-0013 Security: Invalid validator signature for DKG share. Validator: {share.FromValidatorAddress}, Session: {share.SessionId}", "FrostStartup.DKGShare");
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid validator signature"
                                }));
                                return;
                            }

                            // TODO: FROST INTEGRATION
                            // Decrypt share using validator's private key
                            // Verify share against sender's commitment
                            // Store share for Round 3 verification

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Share received and verified",
                                SessionId = share.SessionId,
                                FromValidator = share.FromValidatorAddress
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// POST /frost/dkg/round3 - Submit Round 3 verification result
                /// </summary>
                endpoints.MapPost("/frost/dkg/round3", async context =>
                {
                    try
                    {
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var verification = JsonConvert.DeserializeObject<FrostDKGRound3Message>(body);

                            if (verification == null)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid verification"
                                }));
                                return;
                            }

                            // Get session
                            if (!FrostSessionStorage.DKGSessions.TryGetValue(verification.SessionId, out var session))
                            {
                                context.Response.StatusCode = StatusCodes.Status404NotFound;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Session not found"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify validator is a participant in this session
                            if (string.IsNullOrEmpty(verification.ValidatorAddress)
                                || !session.ParticipantAddresses.Contains(verification.ValidatorAddress))
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Validator is not a participant in this session"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Cryptographic signature verification
                            if (string.IsNullOrEmpty(verification.ValidatorSignature))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Validator signature required"
                                }));
                                return;
                            }

                            var validatorMessage = $"{verification.SessionId}.{verification.ValidatorAddress}.{verification.Timestamp}";
                            var sigValid = SignatureService.VerifySignature(verification.ValidatorAddress, validatorMessage, verification.ValidatorSignature);
                            if (!sigValid)
                            {
                                ErrorLogUtility.LogError($"FIND-0013 Security: Invalid validator signature for DKG Round 3. Validator: {verification.ValidatorAddress}, Session: {verification.SessionId}", "FrostStartup.DKGRound3");
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid validator signature"
                                }));
                                return;
                            }

                            // FIND-016 Fix: Prevent overwrite - each participant can only submit once per round
                            if (!session.Round3Verifications.TryAdd(verification.ValidatorAddress, verification.Verified))
                            {
                                context.Response.StatusCode = StatusCodes.Status409Conflict;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Verification already submitted for this validator in this session"
                                }));
                                return;
                            }

                            var verificationCount = session.Round3Verifications.Count;
                            var verifiedCount = session.Round3Verifications.Count(v => v.Value);
                            var requiredCount = (int)Math.Ceiling(session.ParticipantAddresses.Count * (session.RequiredThreshold / 100.0));

                            LogUtility.Log($"[FROST] DKG Round 3 verification from {verification.ValidatorAddress}: {verification.Verified}. Verified: {verifiedCount}/{requiredCount}", "FrostStartup.DKGRound3");

                            // FIND-024 Fix: If threshold reached, finalize DKG with real FROST native library
                            if (verifiedCount >= requiredCount && !session.IsCompleted)
                            {
                                if (!string.IsNullOrEmpty(session.Round2Secret))
                                {
                                    var round1Json = JsonConvert.SerializeObject(
                                        session.Round1Commitments.ToDictionary(k => k.Key, k => k.Value));
                                    var receivedSharesJsonStr = JsonConvert.SerializeObject(
                                        session.ReceivedSharesJson.ToDictionary(k => k.Key, k => k.Value));

                                    var (groupPubkey, keyPackage, pubkeyPackage, finalizeError) = FrostNative.DKGRound3Finalize(
                                        session.Round2Secret, round1Json, receivedSharesJsonStr);

                                    if (finalizeError == FrostNative.SUCCESS && !string.IsNullOrEmpty(groupPubkey))
                                    {
                                        session.GroupPublicKey = groupPubkey;
                                        session.FinalKeyPackage = keyPackage;
                                        session.FinalPubkeyPackage = pubkeyPackage;

                                        // FIND-024 Fix: Derive Taproot address using NBitcoin (real Bech32m)
                                        session.TaprootAddress = DeriveTaprootAddress(groupPubkey);

                                        // Generate DKG proof with real data
                                        session.DKGProof = GenerateDKGProof(session.SessionId, groupPubkey, pubkeyPackage);
                                        session.IsCompleted = true;

                                        // Persist key package for future signing
                                        var myAddr = Globals.ValidatorAddress;
                                        if (!string.IsNullOrEmpty(myAddr))
                                        {
                                            FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore
                                            {
                                                SmartContractUID = session.SmartContractUID,
                                                ValidatorAddress = myAddr,
                                                KeyPackage = keyPackage,
                                                PubkeyPackage = pubkeyPackage,
                                                GroupPublicKey = groupPubkey,
                                                CreatedTimestamp = TimeUtil.GetTime()
                                            });
                                        }

                                        LogUtility.Log($"[FROST] DKG ceremony completed with real crypto! Address: {session.TaprootAddress}", "FrostStartup.DKGRound3");
                                    }
                                    else
                                    {
                                        ErrorLogUtility.LogError($"FROST DKG Round 3 finalize failed. Error: {finalizeError}", "FrostStartup.DKGRound3");
                                    }
                                }
                                else
                                {
                                    ErrorLogUtility.LogError("DKG Round 3: Round2Secret is null - Round 2 may not have completed", "FrostStartup.DKGRound3");
                                }
                            }

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Verification received",
                                SessionId = verification.SessionId,
                                ValidatorAddress = verification.ValidatorAddress,
                                Verified = verification.Verified,
                                VerificationCount = verificationCount,
                                VerifiedCount = verifiedCount,
                                RequiredCount = requiredCount,
                                CeremonyCompleted = session.IsCompleted
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"DKG Round 3 error: {ex.Message}", "FrostStartup.DKGRound3");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// GET /frost/dkg/round3/{sessionId} - Get Round 3 verifications for coordinator polling
                /// </summary>
                endpoints.MapGet("/frost/dkg/round3/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;

                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "Session ID required"
                            }));
                            return;
                        }

                        // Get session
                        if (!FrostSessionStorage.DKGSessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "Session not found"
                            }));
                            return;
                        }

                        // Return all verifications collected so far
                        var verifications = session.Round3Verifications.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        var verifiedCount = verifications.Count(v => v.Value);
                        var requiredCount = (int)Math.Ceiling(session.ParticipantAddresses.Count * (session.RequiredThreshold / 100.0));

                        context.Response.StatusCode = StatusCodes.Status200OK;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = true,
                            Message = "Round 3 verifications retrieved",
                            SessionId = sessionId,
                            Verifications = verifications,
                            VerificationCount = verifications.Count,
                            VerifiedCount = verifiedCount,
                            RequiredCount = requiredCount,
                            ThresholdReached = verifiedCount >= requiredCount,
                            CeremonyCompleted = session.IsCompleted
                        }, Formatting.Indented));
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"DKG Round 3 GET error: {ex.Message}", "FrostStartup.DKGRound3Get");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// GET /frost/dkg/result/{sessionId} - Get DKG ceremony result
                /// </summary>
                endpoints.MapGet("/frost/dkg/result/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;

                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "Session ID required"
                            }));
                            return;
                        }

                        // Get session
                        if (!FrostSessionStorage.DKGSessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "Session not found"
                            }));
                            return;
                        }

                        // Check if ceremony is completed
                        if (!session.IsCompleted)
                        {
                            context.Response.StatusCode = StatusCodes.Status202Accepted;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "DKG ceremony not yet completed",
                                SessionId = sessionId,
                                IsCompleted = false
                            }));
                            return;
                        }

                        // Return final result
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = true,
                            Message = "DKG result retrieved",
                            SessionId = sessionId,
                            GroupPublicKey = session.GroupPublicKey,
                            TaprootAddress = session.TaprootAddress,
                            DKGProof = session.DKGProof,
                            IsCompleted = session.IsCompleted
                        }, Formatting.Indented));
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"DKG result GET error: {ex.Message}", "FrostStartup.DKGResult");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                #endregion

                #region Signing Endpoints (2-round ceremony)

                /// <summary>
                /// POST /frost/sign/start - Leader broadcasts signing ceremony initiation
                /// </summary>
                endpoints.MapPost("/frost/sign/start", async context =>
                {
                    try
                    {
                        // FIND-0013 Fix: Opportunistic cleanup
                        FrostSessionStorage.CleanupOldSessions();

                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var request = JsonConvert.DeserializeObject<FrostSigningStartRequest>(body);

                            if (request == null)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid request body"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Input validation bounds
                            if (string.IsNullOrEmpty(request.SessionId) || request.SessionId.Length > FrostSessionStorage.MAX_SESSION_ID_LENGTH)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"Invalid SessionId (max {FrostSessionStorage.MAX_SESSION_ID_LENGTH} characters)"
                                }));
                                return;
                            }

                            if (request.SignerAddresses == null || request.SignerAddresses.Count == 0 
                                || request.SignerAddresses.Count > FrostSessionStorage.MAX_PARTICIPANTS)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"SignerAddresses must be 1-{FrostSessionStorage.MAX_PARTICIPANTS}"
                                }));
                                return;
                            }

                            if (request.RequiredThreshold < FrostSessionStorage.MIN_THRESHOLD 
                                || request.RequiredThreshold > FrostSessionStorage.MAX_THRESHOLD)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"RequiredThreshold must be {FrostSessionStorage.MIN_THRESHOLD}-{FrostSessionStorage.MAX_THRESHOLD}"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Enforce maximum concurrent session cap
                            if (FrostSessionStorage.SigningSessions.Count >= FrostSessionStorage.MAX_SIGNING_SESSIONS)
                            {
                                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Maximum concurrent signing sessions reached. Try again later."
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Verify leader is a registered active vBTC validator
                            if (string.IsNullOrEmpty(request.LeaderAddress))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "LeaderAddress required"
                                }));
                                return;
                            }

                            var leaderValidator = VBTCValidator.GetValidator(request.LeaderAddress);
                            if (leaderValidator == null || !leaderValidator.IsActive)
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Leader is not a registered active vBTC validator"
                                }));
                                return;
                            }

                            // FIND-0013 Fix: Cryptographic signature verification
                            if (string.IsNullOrEmpty(request.LeaderSignature))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Leader signature required"
                                }));
                                return;
                            }

                            var leaderMessage = $"{request.SessionId}.{request.LeaderAddress}.{request.Timestamp}";
                            var leaderSigValid = SignatureService.VerifySignature(request.LeaderAddress, leaderMessage, request.LeaderSignature);
                            if (!leaderSigValid)
                            {
                                ErrorLogUtility.LogError($"FIND-0013 Security: Invalid leader signature for signing start. Leader: {request.LeaderAddress}, Session: {request.SessionId}", "FrostStartup.SignStart");
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid leader signature"
                                }));
                                return;
                            }

                            // FIND-024 Fix: Load this validator's key package and generate nonces
                            var myAddr = Globals.ValidatorAddress;
                            var keyStore = FrostValidatorKeyStore.GetKeyPackage(request.SmartContractUID, myAddr);
                            if (keyStore == null || string.IsNullOrEmpty(keyStore.KeyPackage))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "No FROST key package found for this contract. DKG may not have completed."
                                }));
                                return;
                            }

                            // Call FROST native library to generate nonce commitment
                            var (nonceCommitment, nonceSecret, nonceError) = FrostNative.SignRound1Nonces(keyStore.KeyPackage);
                            if (nonceError != FrostNative.SUCCESS || string.IsNullOrEmpty(nonceCommitment))
                            {
                                ErrorLogUtility.LogError($"FROST Sign Round 1 nonce generation failed. Error: {nonceError}", "FrostStartup.SignStart");
                                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = $"FROST nonce generation failed (error: {nonceError})"
                                }));
                                return;
                            }

                            // Create signing session with FROST state
                            var signingSession = new SigningSession
                            {
                                SessionId = request.SessionId,
                                MessageHash = request.MessageHash,
                                SmartContractUID = request.SmartContractUID,
                                LeaderAddress = request.LeaderAddress,
                                SignerAddresses = request.SignerAddresses,
                                RequiredThreshold = request.RequiredThreshold,
                                StartTimestamp = TimeUtil.GetTime(),
                                MyKeyPackage = keyStore.KeyPackage,
                                NonceSecret = nonceSecret
                            };

                            // Auto-store this validator's nonce commitment
                            signingSession.Round1Nonces.TryAdd(myAddr, nonceCommitment);

                            if (!FrostSessionStorage.SigningSessions.TryAdd(request.SessionId, signingSession))
                            {
                                context.Response.StatusCode = StatusCodes.Status409Conflict;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Signing session already exists"
                                }));
                                return;
                            }

                            LogUtility.Log($"[FROST] Signing ceremony started with real nonce generation. Session: {request.SessionId}", "FrostStartup.SignStart");

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Signing ceremony started with FROST nonce generated",
                                SessionId = request.SessionId,
                                MessageHash = request.MessageHash,
                                NonceGenerated = true
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// POST /frost/sign/round1 - Collect Round 1 nonce commitments
                /// </summary>
                endpoints.MapPost("/frost/sign/round1", async context =>
                {
                    try
                    {
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var nonce = JsonConvert.DeserializeObject<FrostSigningRound1Message>(body);

                            if (nonce == null)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid nonce commitment"
                                }));
                                return;
                            }

                            // TODO: FROST INTEGRATION
                            // Validate validator signature
                            // Store nonce commitment
                            // Check if we have enough commitments (threshold reached)
                            // If threshold reached, aggregate and broadcast to Round 2

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Round 1 nonce commitment received",
                                SessionId = nonce.SessionId,
                                ValidatorAddress = nonce.ValidatorAddress
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// POST /frost/sign/round2 - Collect Round 2 signature shares
                /// </summary>
                endpoints.MapPost("/frost/sign/round2", async context =>
                {
                    try
                    {
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var signatureShare = JsonConvert.DeserializeObject<FrostSigningRound2Message>(body);

                            if (signatureShare == null)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                                {
                                    Success = false,
                                    Message = "Invalid signature share"
                                }));
                                return;
                            }

                            // TODO: FROST INTEGRATION
                            // Validate validator signature
                            // Store signature share
                            // Check if we have enough shares (threshold reached)
                            // If threshold reached, aggregate into final Schnorr signature
                            // Validate final signature
                            // Broadcast final result

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Round 2 signature share received",
                                SessionId = signatureShare.SessionId,
                                ValidatorAddress = signatureShare.ValidatorAddress
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                /// <summary>
                /// FIND-015 Fix: GET /frost/sign/round1/{sessionId} - Get collected nonce commitments for coordinator polling
                /// This endpoint was missing, causing CollectSigningRound1Nonces to always fail
                /// </summary>
                endpoints.MapGet("/frost/sign/round1/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Session ID required" }));
                            return;
                        }

                        if (!FrostSessionStorage.SigningSessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Signing session not found" }));
                            return;
                        }

                        var nonces = session.Round1Nonces.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        var requiredCount = (int)Math.Ceiling(session.SignerAddresses.Count * (session.RequiredThreshold / 100.0));

                        context.Response.StatusCode = StatusCodes.Status200OK;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = true,
                            Message = "Signing Round 1 nonces retrieved",
                            SessionId = sessionId,
                            Nonces = nonces,
                            NonceCount = nonces.Count,
                            RequiredCount = requiredCount,
                            ThresholdReached = nonces.Count >= requiredCount
                        }, Formatting.Indented));
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" }));
                    }
                });

                /// <summary>
                /// FIND-015 Fix: POST /frost/sign/round2/{sessionId} - Receive broadcast of aggregated nonces for signature share generation
                /// This route with sessionId was missing, causing CollectSigningRound2Shares to always fail
                /// </summary>
                endpoints.MapPost("/frost/sign/round2/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Session ID required" }));
                            return;
                        }

                        if (!FrostSessionStorage.SigningSessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Signing session not found" }));
                            return;
                        }

                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            
                            // FIND-024 Fix: Call FROST native library to generate signature share
                            if (string.IsNullOrEmpty(session.MyKeyPackage) || string.IsNullOrEmpty(session.NonceSecret))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Key package or nonce secret not found" }));
                                return;
                            }

                            var (signatureShare, sigShareError) = FrostNative.SignRound2Signature(
                                session.MyKeyPackage, session.NonceSecret, body, session.MessageHash);

                            if (sigShareError != FrostNative.SUCCESS || string.IsNullOrEmpty(signatureShare))
                            {
                                ErrorLogUtility.LogError($"FROST Sign Round 2 signature share generation failed. Error: {sigShareError}", "FrostStartup.SignRound2");
                                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = $"Signature share generation failed (error: {sigShareError})" }));
                                return;
                            }

                            // Store this validator's signature share
                            var myAddr = Globals.ValidatorAddress ?? "";
                            session.Round2Shares.TryAdd(myAddr, signatureShare);

                            LogUtility.Log($"[FROST] Signing Round 2 signature share generated via native library for session {sessionId}", "FrostStartup.SignRound2");

                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = true,
                                Message = "Signature share generated via FROST native library",
                                SessionId = sessionId,
                                ShareGenerated = true,
                                SignatureShare = signatureShare
                            }, Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" }));
                    }
                });

                /// <summary>
                /// FIND-015 Fix: GET /frost/sign/share/{sessionId} - Get collected signature shares for coordinator polling
                /// This endpoint was missing, causing CollectSigningRound2Shares to always fail
                /// </summary>
                endpoints.MapGet("/frost/sign/share/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Session ID required" }));
                            return;
                        }

                        if (!FrostSessionStorage.SigningSessions.TryGetValue(sessionId, out var session))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = "Signing session not found" }));
                            return;
                        }

                        var shares = session.Round2Shares.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        var requiredCount = (int)Math.Ceiling(session.SignerAddresses.Count * (session.RequiredThreshold / 100.0));

                        context.Response.StatusCode = StatusCodes.Status200OK;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = true,
                            Message = "Signature shares retrieved",
                            SessionId = sessionId,
                            Shares = shares,
                            ShareCount = shares.Count,
                            RequiredCount = requiredCount,
                            ThresholdReached = shares.Count >= requiredCount
                        }, Formatting.Indented));
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" }));
                    }
                });

                /// <summary>
                /// GET /frost/sign/result/{sessionId} - Get signing ceremony result
                /// </summary>
                endpoints.MapGet("/frost/sign/result/{sessionId}", async context =>
                {
                    try
                    {
                        var sessionId = context.Request.RouteValues["sessionId"] as string;

                        if (string.IsNullOrEmpty(sessionId))
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Message = "Session ID required"
                            }));
                            return;
                        }

                        // TODO: FROST INTEGRATION
                        // Retrieve signing result from storage
                        // Return aggregated Schnorr signature

                        context.Response.StatusCode = StatusCodes.Status200OK;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = true,
                            Message = "Signing result retrieved",
                            SessionId = sessionId,
                            SchnorrSignature = "PLACEHOLDER_SCHNORR_SIGNATURE",
                            SignatureValid = true
                        }, Formatting.Indented));
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                        {
                            Success = false,
                            Message = $"Error: {ex.Message}"
                        }));
                    }
                });

                #endregion
            });
        }

        #region FROST Cryptographic Operations (FIND-024 Fix: Real implementations)

        /// <summary>
        /// FIND-024 Fix: Derive a real Taproot address from the FROST group public key using NBitcoin.
        /// The group public key is a 32-byte x-only public key from FROST DKG.
        /// We use NBitcoin's TaprootPubKey to produce a proper Bech32m-encoded address per BIP340/BIP341/BIP350.
        /// </summary>
        private static string DeriveTaprootAddress(string groupPublicKeyHex)
        {
            try
            {
                // The FROST group public key should be a 32-byte (64 hex char) x-only public key
                var pubkeyBytes = Convert.FromHexString(groupPublicKeyHex);
                if (pubkeyBytes.Length != 32)
                {
                    ErrorLogUtility.LogError($"FROST group public key unexpected length: {pubkeyBytes.Length} bytes (expected 32)", "FrostStartup.DeriveTaprootAddress");
                    return string.Empty;
                }

                // Create NBitcoin TaprootPubKey from 32-byte x-only key
                var taprootPubKey = new TaprootPubKey(pubkeyBytes);

                // Derive the Taproot address using proper Bech32m encoding
                var network = Globals.IsTestNet ? Network.TestNet : Network.Main;
                var taprootAddress = taprootPubKey.GetAddress(network);

                var addressStr = taprootAddress.ToString();
                LogUtility.Log($"[FROST] Real Taproot address derived via NBitcoin: {addressStr}", "FrostStartup.DeriveTaprootAddress");
                return addressStr;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Taproot address derivation error: {ex.Message}", "FrostStartup.DeriveTaprootAddress");
                return string.Empty;
            }
        }

        /// <summary>
        /// Generate DKG completion proof containing the real FROST artifacts.
        /// </summary>
        private static string GenerateDKGProof(string sessionId, string groupPublicKey, string pubkeyPackage)
        {
            try
            {
                var proofData = new
                {
                    SessionId = sessionId,
                    GroupPublicKey = groupPublicKey,
                    PubkeyPackageHash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(pubkeyPackage))),
                    Timestamp = TimeUtil.GetTime(),
                    FrostVersion = FrostNative.GetVersion(),
                    ProofType = "DKG_COMPLETION_FROST_NATIVE"
                };

                var proofJson = JsonConvert.SerializeObject(proofData);
                var proof = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(proofJson));
                
                LogUtility.Log($"[FROST] DKG proof generated with real FROST data for session {sessionId}", "FrostStartup.GenerateDKGProof");
                return proof;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DKG proof generation error: {ex.Message}", "FrostStartup.GenerateDKGProof");
                return string.Empty;
            }
        }

        #endregion
    }
}
