using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.DST;
using ReserveBlockCore.Engines;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.DST;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Net;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class ShopsController : RestBaseController
    {
        #region Local Shop CRUD

        /// <summary>
        /// Get local shop info
        /// </summary>
        [HttpGet]
        public IActionResult GetShop()
        {
            var decshop = DecShop.GetMyDecShopInfo();
            if (decshop == null)
                return Fail("NOT_FOUND", "No local DecShop found.", 404);

            return Ok(decshop);
        }

        /// <summary>
        /// Create or update a local shop
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SaveShop([FromBody] object jsonData)
        {
            var myDS = DecShop.GetMyDecShopInfo();
            var decShop = JsonConvert.DeserializeObject<DecShop>(jsonData.ToString()!);
            if (decShop == null)
                return Fail("INVALID_DATA", "Failed to deserialize JSON payload.");

            if (myDS == null)
            {
                var wordCount = decShop.Description.ToWordCountCheck(200);
                var descLength = decShop.Description.ToLengthCheck(1200);
                if (!wordCount || !descLength)
                    return Fail("VALIDATION_ERROR", $"Description word count allowed: 200. Description length allowed: 1200.");

                var urlCheck = DecShop.ValidStateTreiURL(decShop.DecShopURL);
                if (!urlCheck)
                    return Fail("URL_TAKEN", $"URL: {decShop.DecShopURL} has already been taken.", 409);

                var buildResult = decShop.Build();
                if (!buildResult.Item1)
                    return Fail("BUILD_FAILED", buildResult.Item2);

                var result = await DecShop.SaveMyDecShopLocal(decShop);
                if (!result.Item1)
                    return Fail("SAVE_FAILED", result.Item2);

                return Created(new { Message = result.Item2 });
            }
            else
            {
                myDS.Name = decShop.Name;
                myDS.Description = decShop.Description;
                myDS.IsOffline = decShop.IsOffline;
                myDS.AutoUpdateNetworkDNS = decShop.AutoUpdateNetworkDNS;
                myDS.HostingType = decShop.HostingType;

                if (myDS.DecShopURL != decShop.DecShopURL)
                    myDS.DecShopURL = $"vfx://{decShop.DecShopURL}";

                if (decShop.HostingType == DecShopHostingType.SelfHosted)
                {
                    myDS.IP = decShop.IP;
                    myDS.Port = decShop.Port;
                    myDS.HostingType = DecShopHostingType.SelfHosted;
                }

                if (myDS.IsIPDifferent && myDS.HostingType == DecShopHostingType.Network)
                {
                    myDS.IP = P2PClient.MostLikelyIP();
                    myDS.Port = myDS.Port == 0 ? Globals.DSTClientPort : myDS.Port;
                }

                if (decShop.HostingType == DecShopHostingType.ThirdParty)
                {
                    myDS.IP = "NA";
                    myDS.Port = 0;
                }

                var result = await DecShop.SaveMyDecShopLocal(myDS);
                if (!result.Item1)
                    return Fail("SAVE_FAILED", result.Item2);

                return Ok(new { Message = result.Item2 });
            }
        }

        /// <summary>
        /// Publish shop to network
        /// </summary>
        [HttpPost("publish")]
        public async Task<IActionResult> Publish()
        {
            var localShop = DecShop.GetMyDecShopInfo();
            if (localShop == null)
                return Fail("NOT_FOUND", "A local DecShop does not exist.", 404);

            if (localShop.IsPublished)
                return Fail("ALREADY_PUBLISHED", "Shop has already been created. Use update instead.", 409);

            var txResult = await DecShop.CreateDecShopTx(localShop);
            if (txResult.Item1 == null)
                return Fail("PUBLISH_FAILED", txResult.Item2);

            return Ok(new { TxHash = txResult.Item1.Hash });
        }

        /// <summary>
        /// Update shop on network
        /// </summary>
        [HttpPost("update")]
        public async Task<IActionResult> UpdateShop()
        {
            var localShop = DecShop.GetMyDecShopInfo();
            if (localShop == null)
                return Fail("NOT_FOUND", "A local DecShop does not exist.", 404);

            if (!localShop.NeedsPublishToNetwork)
                return Fail("NO_UPDATE", "No update is pending.");

            var txResult = await DecShop.UpdateDecShopTx(localShop);
            if (txResult.Item1 == null)
                return Fail("UPDATE_FAILED", txResult.Item2);

            return Ok(new { TxHash = txResult.Item1.Hash });
        }

        /// <summary>
        /// Toggle shop online/offline status
        /// </summary>
        [HttpPost("status/toggle")]
        public IActionResult ToggleStatus()
        {
            var decshop = DecShop.GetMyDecShopInfo();
            if (decshop == null)
                return Fail("NOT_FOUND", "No local DecShop found.", 404);

            var result = DecShop.SetDecShopStatus();
            if (result == null)
                return Fail("TOGGLE_FAILED", "Failed to toggle shop status.");

            return Ok(new { IsOffline = result.Value });
        }

        /// <summary>
        /// Delete shop from network
        /// </summary>
        [HttpDelete]
        public async Task<IActionResult> DeleteShop()
        {
            var localShop = DecShop.GetMyDecShopInfo();
            if (localShop == null)
                return Fail("NOT_FOUND", "A local DecShop does not exist.", 404);

            var txResult = await DecShop.DeleteDecShopTx(localShop.UniqueId, localShop.OwnerAddress);
            if (txResult.Item1 == null)
                return Fail("DELETE_FAILED", txResult.Item2);

            return Ok(new { TxHash = txResult.Item1.Hash });
        }

        /// <summary>
        /// Delete local shop and all associated data
        /// </summary>
        [HttpDelete("local")]
        public async Task<IActionResult> DeleteLocalShop()
        {
            var localShop = DecShop.GetMyDecShopInfo();
            if (localShop == null)
                return Fail("NOT_FOUND", "A local DecShop does not exist.", 404);

            var decDb = DecShop.DecShopLocalDB();
            if (decDb == null)
                return Fail("DB_ERROR", "Could not access DecShop database.");

            var result = decDb.DeleteSafe(localShop.Id);
            var listingDeleteResult = await Listing.DeleteAllListingsByCollection(localShop.Id);
            var auctionsDeleteResult = await Auction.DeleteAllAuctionsByCollection(localShop.Id);
            var bidDeleteResult = await Bid.DeleteAllBidsByCollection(localShop.Id);

            return Ok(new
            {
                ShopDeleted = result,
                ListingsDeleted = listingDeleteResult.Item1,
                AuctionsDeleted = auctionsDeleteResult.Item1,
                BidsDeleted = bidDeleteResult.Item1
            });
        }

        /// <summary>
        /// Import a shop from the network by owner address
        /// </summary>
        [HttpPost("import/{address}")]
        public async Task<IActionResult> ImportFromNetwork(string address)
        {
            var dcStateTreiDb = DecShop.DecShopTreiDb();
            var leaf = dcStateTreiDb?.Query().Where(x => x.OwnerAddress == address).FirstOrDefault();
            if (leaf == null)
                return Fail("NOT_FOUND", $"Could not find the DecShop leaf for address: {address}.", 404);

            var localAddress = AccountData.GetSingleAccount(address);
            if (localAddress == null)
                return Fail("NOT_OWNER", "You do not own this address and cannot import shop.", 403);

            var decShopExist = DecShop.GetMyDecShopInfo();
            if (decShopExist != null)
                return Fail("ALREADY_EXISTS", "Wallet already has a dec shop associated to it.", 409);

            leaf.Id = 0;
            var result = await DecShop.SaveMyDecShopLocal(leaf, false, true);
            if (!result.Item1)
                return Fail("IMPORT_FAILED", result.Item2);

            return Ok(new { Message = result.Item2 });
        }

        #endregion

        #region Network Shop Discovery

        /// <summary>
        /// Search for a shop by URL on the network
        /// </summary>
        [HttpGet("network/search")]
        public async Task<IActionResult> SearchByUrl([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return Fail("VALIDATION_ERROR", "URL query parameter is required.");

            url = WebUtility.UrlDecode(url);
            var decshop = await DecShop.GetDecShopStateTreiLeafByURL(url);
            if (decshop == null)
                return Fail("NOT_FOUND", "No DecShop found.", 404);

            return Ok(decshop);
        }

        /// <summary>
        /// Get network shop info by URL
        /// </summary>
        [HttpGet("network/info")]
        public async Task<IActionResult> GetNetworkInfo([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return Fail("VALIDATION_ERROR", "URL query parameter is required.");

            url = WebUtility.UrlDecode(url);
            var decshop = await DecShop.GetDecShopStateTreiLeafByURL(url);
            if (decshop == null)
                return Fail("NOT_FOUND", $"Could not find the DecShop leaf for url: {url}.", 404);

            return Ok(decshop);
        }

        /// <summary>
        /// List all shops on the network
        /// </summary>
        [HttpGet("network/list")]
        public async Task<IActionResult> GetNetworkList()
        {
            var decshops = await DecShop.GetDecShopStateTreiList();
            if (decshops == null || !decshops.Any())
                return Ok(Array.Empty<object>());

            return Ok(decshops);
        }

        #endregion

        #region Shop Connections

        /// <summary>
        /// Connect to a remote shop
        /// </summary>
        [HttpPost("connect")]
        public async Task<IActionResult> Connect([FromBody] ConnectToShopRequest request)
        {
            var decshop = await DecShop.GetDecShopStateTreiLeafByURL(request.Url);
            if (decshop == null)
                return Fail("NOT_FOUND", "DecShop not found on the network.", 404);

            var accountExist = AccountData.GetSingleAccount(request.Address);
            if (accountExist == null)
                return Fail("NOT_OWNER", "You must own the connecting address.", 403);

            await DSTClient.DisconnectFromShop();
            var connectionResult = await DSTClient.ConnectToShop(request.Url, request.Address);

            if (connectionResult)
                _ = DSTClient.GetShopData(request.Address);

            return Ok(new { Connected = connectionResult });
        }

        /// <summary>
        /// Get current shop connections
        /// </summary>
        [HttpGet("connections")]
        public IActionResult GetConnections()
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (connectedShop.Any())
            {
                var decShop = connectedShop.FirstOrDefault().Value;
                if (decShop != null)
                    return Ok(new { DecShop = decShop, Connected = true });
            }

            return Ok(new { DecShop = (object?)null, Connected = false });
        }

        /// <summary>
        /// Get cached shop data from memory
        /// </summary>
        [HttpGet("data")]
        public IActionResult GetShopData()
        {
            if (Globals.DecShopData != null)
                return Ok(Globals.DecShopData);

            return Fail("NOT_FOUND", "Data not found.", 404);
        }

        /// <summary>
        /// Ping a connected shop
        /// </summary>
        [HttpPost("ping/{pingId}")]
        public async Task<IActionResult> PingShop(string pingId)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            var result = await DSTClient.PingConnection(pingId);
            if (!result)
                return Fail("PING_FAILED", "Ping attempt failed.");

            Globals.PingResultDict.TryGetValue(pingId, out var pingResult);
            return Ok(new { PingId = pingId, Result = pingResult });
        }

        /// <summary>
        /// Check ping result
        /// </summary>
        [HttpGet("ping/{pingId}")]
        public IActionResult CheckPing(string pingId)
        {
            if (Globals.PingResultDict.TryGetValue(pingId, out var value))
                return Ok(new { PingId = pingId, Result = value });

            return Fail("NOT_FOUND", "Could not find that PingId.", 404);
        }

        /// <summary>
        /// Clear all ping requests
        /// </summary>
        [HttpDelete("ping")]
        public IActionResult ClearPings()
        {
            Globals.PingResultDict.Clear();
            return Ok(new { Message = "Pings cleared." });
        }

        #endregion

        #region Remote Shop Queries

        /// <summary>
        /// Request shop info from connected shop
        /// </summary>
        [HttpGet("remote/info")]
        public IActionResult GetRemoteShopInfo()
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.Info}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Shop info request sent." });
        }

        /// <summary>
        /// Request collections from connected shop
        /// </summary>
        [HttpGet("remote/collections")]
        public IActionResult GetRemoteCollections()
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.Collections}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Collections request sent." });
        }

        /// <summary>
        /// Request listings from connected shop
        /// </summary>
        [HttpGet("remote/listings/{page:int}")]
        public IActionResult GetRemoteListings(int page)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.Listings},{page}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Listings request sent." });
        }

        /// <summary>
        /// Request auctions from connected shop
        /// </summary>
        [HttpGet("remote/auctions/{page:int}")]
        public IActionResult GetRemoteAuctions(int page)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.Auctions},{page}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Auctions request sent." });
        }

        /// <summary>
        /// Request listings by collection from connected shop
        /// </summary>
        [HttpGet("remote/listings/collection/{collectionId:int}/{page:int}")]
        public IActionResult GetRemoteListingsByCollection(int collectionId, int page)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.ListingsByCollection},{collectionId},{page}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Listings request sent." });
        }

        /// <summary>
        /// Request a specific listing from connected shop
        /// </summary>
        [HttpGet("remote/listings/specific/{scUID}")]
        public IActionResult GetRemoteSpecificListing(string scUID)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.SpecificListing},{scUID}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Specific listing request sent." });
        }

        /// <summary>
        /// Request a specific auction from connected shop
        /// </summary>
        [HttpGet("remote/auctions/specific/{listingId}")]
        public IActionResult GetRemoteSpecificAuction(string listingId)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.SpecificAuction},{listingId}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Specific auction request sent." });
        }

        /// <summary>
        /// Request bids for a listing from connected shop
        /// </summary>
        [HttpGet("remote/bids/{listingId:int}")]
        public IActionResult GetRemoteListingBids(int listingId)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            Message message = new Message
            {
                Data = $"{DecShopRequestOptions.Bids},{listingId}",
                Type = MessageType.DecShop,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, true);
            return Ok(new { Message = "Listing bids request sent." });
        }

        /// <summary>
        /// Request NFT asset download from connected shop
        /// </summary>
        [HttpPost("remote/assets/{scUID}")]
        public async Task<IActionResult> DownloadAssets(string scUID)
        {
            var connectedShop = Globals.ConnectedClients.Where(x => x.Value.IsConnected).Take(1);
            if (!connectedShop.Any())
                return Fail("NOT_CONNECTED", "Not connected to a shop.");

            if (Globals.AssetDownloadLock)
                return Fail("LOCKED", "Asset download is currently locked.");

            Globals.AssetDownloadLock = true;
            await DSTClient.DisconnectFromAsset();
            var connected = await DSTClient.ConnectToShopForAssets();
            if (!connected)
            {
                Globals.AssetDownloadLock = false;
                return Fail("CONNECTION_FAILED", "Failed to connect for asset download.");
            }

            Message message = new Message
            {
                Data = scUID,
                Type = MessageType.AssetReq,
                ComType = MessageComType.Info
            };

            _ = DSTClient.GetListingAssetThumbnails(message, scUID);
            return Ok(new { Message = "Asset download started." });
        }

        #endregion

        #region Collections

        /// <summary>
        /// List all collections
        /// </summary>
        [HttpGet("collections")]
        public IActionResult GetCollections()
        {
            var collections = Collection.GetAllCollections();
            if (collections == null || !collections.Any())
                return Ok(Array.Empty<object>());

            return Ok(collections);
        }

        /// <summary>
        /// Get a single collection
        /// </summary>
        [HttpGet("collections/{collectionId:int}")]
        public IActionResult GetCollection(int collectionId)
        {
            if (collectionId == 0)
                return Fail("VALIDATION_ERROR", "Collection ID cannot be 0.");

            var collection = Collection.GetSingleCollection(collectionId);
            if (collection == null)
                return Fail("NOT_FOUND", "Collection was not found.", 404);

            return Ok(collection);
        }

        /// <summary>
        /// Get the default collection
        /// </summary>
        [HttpGet("collections/default")]
        public IActionResult GetDefaultCollection()
        {
            var collection = Collection.GetDefaultCollection();
            if (collection == null)
                return Fail("NOT_FOUND", "Default collection was not found.", 404);

            return Ok(collection);
        }

        /// <summary>
        /// Change the default collection
        /// </summary>
        [HttpPost("collections/{collectionId:int}/set-default")]
        public IActionResult SetDefaultCollection(int collectionId)
        {
            if (collectionId == 0)
                return Fail("VALIDATION_ERROR", "Collection ID cannot be 0.");

            var result = Collection.ChangeDefaultCollection(collectionId);
            if (!result.Item1)
                return Fail("CHANGE_FAILED", result.Item2);

            return Ok(new { Message = result.Item2 });
        }

        /// <summary>
        /// Create or update a collection
        /// </summary>
        [HttpPost("collections")]
        public async Task<IActionResult> SaveCollection([FromBody] object jsonData)
        {
            var collection = JsonConvert.DeserializeObject<Collection>(jsonData.ToString()!);
            if (collection == null)
                return Fail("INVALID_DATA", "Failed to deserialize JSON payload.");

            var result = await Collection.SaveCollection(collection);
            if (!result.Item1)
                return Fail("SAVE_FAILED", result.Item2);

            return Created(new { Message = result.Item2 });
        }

        /// <summary>
        /// Delete a collection and all associated data
        /// </summary>
        [HttpDelete("collections/{collectionId:int}")]
        public async Task<IActionResult> DeleteCollection(int collectionId)
        {
            if (collectionId == 0)
                return Fail("VALIDATION_ERROR", "Collection ID cannot be 0.");

            var listings = Listing.GetCollectionListings(collectionId);
            var result = await Collection.DeleteCollection(collectionId);
            if (!result.Item1)
                return Fail("DELETE_FAILED", result.Item2);

            if (listings?.Count() > 0)
            {
                await Listing.DeleteAllListingsByCollection(collectionId);
                await Auction.DeleteAllAuctionsByCollection(collectionId);
                await Bid.DeleteAllBidsByCollection(collectionId);
            }

            return Ok(new { Message = result.Item2 });
        }

        #endregion

        #region Listings

        /// <summary>
        /// Get listings by collection
        /// </summary>
        [HttpGet("listings/{collectionId:int}")]
        public IActionResult GetCollectionListings(int collectionId)
        {
            if (collectionId == 0)
                return Fail("VALIDATION_ERROR", "Collection ID cannot be 0.");

            var listings = Listing.GetCollectionListings(collectionId);
            if (listings == null || !listings.Any())
                return Ok(Array.Empty<object>());

            return Ok(listings);
        }

        /// <summary>
        /// Get a single listing with auction and bids
        /// </summary>
        [HttpGet("listings/single/{listingId:int}")]
        public IActionResult GetSingleListing(int listingId)
        {
            if (listingId == 0)
                return Fail("VALIDATION_ERROR", "Listing ID cannot be 0.");

            var listing = Listing.GetSingleListing(listingId);
            if (listing == null)
                return Fail("NOT_FOUND", "Listing was not found.", 404);

            var auction = Auction.GetListingAuction(listingId);
            var bids = Bid.GetListingBids(listingId);

            return Ok(new { Listing = listing, Auction = auction, Bids = bids });
        }

        /// <summary>
        /// Create or update a listing
        /// </summary>
        [HttpPost("listings")]
        public async Task<IActionResult> SaveListing([FromBody] object jsonData)
        {
            var listing = JsonConvert.DeserializeObject<Listing>(jsonData.ToString()!);
            if (listing == null)
                return Fail("INVALID_DATA", "Failed to deserialize JSON payload.");

            var result = await Listing.SaveListing(listing);
            if (!result.Item1)
                return Fail("SAVE_FAILED", result.Item2);

            return Created(new { Message = result.Item2 });
        }

        /// <summary>
        /// Cancel a listing
        /// </summary>
        [HttpPost("listings/{listingId:int}/cancel")]
        public async Task<IActionResult> CancelListing(int listingId)
        {
            if (listingId == 0)
                return Fail("VALIDATION_ERROR", "Listing ID cannot be 0.");

            var listing = Listing.GetSingleListing(listingId);
            if (listing == null)
                return Fail("NOT_FOUND", "No listing found.", 404);

            listing.IsCancelled = true;
            var result = await Listing.SaveListing(listing);
            if (!result.Item1)
                return Fail("CANCEL_FAILED", result.Item2);

            return Ok(new { Message = result.Item2 });
        }

        /// <summary>
        /// Delete a listing and all associated data
        /// </summary>
        [HttpDelete("listings/{listingId:int}")]
        public async Task<IActionResult> DeleteListing(int listingId)
        {
            if (listingId == 0)
                return Fail("VALIDATION_ERROR", "Listing ID cannot be 0.");

            var result = await Listing.DeleteListing(listingId);
            if (!result.Item1)
                return Fail("DELETE_FAILED", result.Item2);

            await Auction.DeleteAllAuctionsByListing(listingId);
            await Bid.DeleteAllBidsByListing(listingId);

            return Ok(new { Message = result.Item2 });
        }

        /// <summary>
        /// Retry a failed sale
        /// </summary>
        [HttpPost("listings/{listingId:int}/retry-sale")]
        public IActionResult RetrySale(int listingId)
        {
            var listingDb = Listing.GetListingDb();
            if (listingDb == null)
                return Fail("DB_ERROR", "Listing DB was null.");

            var singleListing = Listing.GetSingleListing(listingId);
            if (singleListing == null)
                return Fail("NOT_FOUND", $"Could not find listing: {listingId}.", 404);

            if (singleListing.IsSaleComplete)
                return Fail("ALREADY_COMPLETE", $"This listing has already completed its sale.");

            singleListing.SaleHasFailed = false;
            AuctionEngine.ListingPostSaleDict.TryRemove(listingId, out _);
            listingDb.UpdateSafe(singleListing);

            return Ok(new { Message = "Listing has been updated for retry." });
        }

        #endregion

        #region Auctions

        /// <summary>
        /// Get auction by listing ID
        /// </summary>
        [HttpGet("auctions/{listingId:int}")]
        public IActionResult GetAuction(int listingId)
        {
            if (listingId == 0)
                return Fail("VALIDATION_ERROR", "Listing ID cannot be 0.");

            var auction = Auction.GetListingAuction(listingId);
            if (auction == null)
                return Fail("NOT_FOUND", "Auction was not found.", 404);

            return Ok(auction);
        }

        /// <summary>
        /// Reset auction ended state
        /// </summary>
        [HttpPost("auctions/{listingId:int}/reset")]
        public IActionResult ResetAuction(int listingId)
        {
            if (listingId == 0)
                return Fail("VALIDATION_ERROR", "Listing ID cannot be 0.");

            var auction = Auction.GetListingAuction(listingId);
            if (auction == null)
                return Fail("NOT_FOUND", "Auction was not found.", 404);

            auction.IsAuctionOver = false;
            Auction.SaveAuction(auction);

            return Ok(auction);
        }

        #endregion

        #region Bids

        /// <summary>
        /// Get all bids
        /// </summary>
        [HttpGet("bids/{sendReceive}")]
        public IActionResult GetBids(BidSendReceive sendReceive)
        {
            var bids = Bid.GetAllBids(sendReceive);
            if (bids == null || !bids.Any())
                return Ok(Array.Empty<object>());

            return Ok(bids);
        }

        /// <summary>
        /// Get bids for a specific listing
        /// </summary>
        [HttpGet("bids/listing/{listingId:int}/{sendReceive}")]
        public IActionResult GetListingBids(int listingId, BidSendReceive sendReceive)
        {
            var bids = Bid.GetListingBids(listingId, sendReceive);
            if (bids == null || !bids.Any())
                return Ok(Array.Empty<object>());

            return Ok(bids);
        }

        /// <summary>
        /// Get bids by status
        /// </summary>
        [HttpGet("bids/status/{bidStatus}/{sendReceive}")]
        public IActionResult GetBidsByStatus(BidStatus bidStatus, BidSendReceive sendReceive)
        {
            var bids = Bid.GetBidByStatus(bidStatus);
            if (bids == null || !bids.Any())
                return Ok(Array.Empty<object>());

            return Ok(bids);
        }

        /// <summary>
        /// Get a single bid
        /// </summary>
        [HttpGet("bids/{bidId:guid}")]
        public IActionResult GetSingleBid(Guid bidId)
        {
            var bid = Bid.GetSingleBid(bidId);
            if (bid == null)
                return Fail("NOT_FOUND", "Bid not found.", 404);

            return Ok(bid);
        }

        /// <summary>
        /// Send a bid
        /// </summary>
        [HttpPost("bids")]
        public IActionResult SendBid([FromBody] object jsonData)
        {
            var bidPayload = JsonConvert.DeserializeObject<Bid>(jsonData.ToString()!);
            if (bidPayload == null)
                return Fail("INVALID_DATA", "Bid payload cannot be null.");

            if (!bidPayload.RawBid)
            {
                var localAddress = AccountData.GetSingleAccount(bidPayload.BidAddress);
                if (localAddress == null)
                    return Fail("NOT_OWNER", "You must own the bid address.", 403);
            }

            if (bidPayload.BidAddress.StartsWith("xRBX"))
                return Fail("INVALID_ADDRESS", "You may not place bids with a reserve account.");

            if (bidPayload.BidStatus != BidStatus.Accepted && bidPayload.BidStatus != BidStatus.Rejected)
            {
                if (Globals.DecShopData?.DecShop == null)
                    return Fail("NO_SHOP_DATA", "DecShop data cannot be null. Connect to a shop first.");
            }

            var thirdPartyBid = bidPayload.BidStatus == BidStatus.Accepted || bidPayload.BidStatus == BidStatus.Rejected;
            var bidBuild = bidPayload.Build(thirdPartyBid);
            if (!bidBuild)
                return Fail("BUILD_FAILED", "Failed to build bid.");

            var result = Bid.SaveBid(bidPayload);
            if (!result.Item1)
                return Fail("BID_FAILED", result.Item2);

            return Created(new { Message = result.Item2, Bid = bidPayload });
        }

        /// <summary>
        /// Send a buy-now bid
        /// </summary>
        [HttpPost("bids/buy-now")]
        public IActionResult SendBuyNowBid([FromBody] object jsonData)
        {
            var bidPayload = JsonConvert.DeserializeObject<Bid>(jsonData.ToString()!);
            if (bidPayload == null)
                return Fail("INVALID_DATA", "Bid payload cannot be null.");

            if (!bidPayload.RawBid)
            {
                var localAddress = AccountData.GetSingleAccount(bidPayload.BidAddress);
                if (localAddress == null)
                    return Fail("NOT_OWNER", "You must own the bid address.", 403);

                if (bidPayload.BidAddress.StartsWith("xRBX"))
                    return Fail("INVALID_ADDRESS", "You may not perform a 'Buy Now' with a reserve account.");
            }

            if (bidPayload.BidStatus != BidStatus.Accepted && bidPayload.BidStatus != BidStatus.Rejected)
            {
                if (Globals.DecShopData?.DecShop == null)
                    return Fail("NO_SHOP_DATA", "DecShop data cannot be null. Connect to a shop first.");
            }

            var thirdPartyBid = bidPayload.BidStatus == BidStatus.Accepted || bidPayload.BidStatus == BidStatus.Rejected;
            var bidBuild = bidPayload.Build(thirdPartyBid);

            if (bidPayload.IsBuyNow != true)
                return Fail("VALIDATION_ERROR", "IsBuyNow must be set to true.");

            if (!bidBuild)
                return Fail("BUILD_FAILED", "Failed to build bid.");

            Bid.SaveBid(bidPayload);
            return Created(new { Message = "Buy Now bid sent.", Bid = bidPayload });
        }

        /// <summary>
        /// Resend an existing bid
        /// </summary>
        [HttpPost("bids/{bidId:guid}/resend")]
        public IActionResult ResendBid(Guid bidId)
        {
            var bid = Bid.GetSingleBid(bidId);
            if (bid == null)
                return Fail("NOT_FOUND", "Bid not found.", 404);

            var bidJson = JsonConvert.SerializeObject(bid);

            Message message = new Message
            {
                Data = bidJson,
                Type = MessageType.Bid,
                ComType = MessageComType.Request
            };

            _ = DSTClient.SendShopMessageFromClient(message, false);
            return Ok(new { Message = "Bid resent.", BidId = bid.Id });
        }

        #endregion

        #region Chat

        /// <summary>
        /// Send a chat message to a connected shop
        /// </summary>
        [HttpPost("chat")]
        public IActionResult SendChatMessage([FromBody] object jsonData)
        {
            var chatPayload = JsonConvert.DeserializeObject<Chat.ChatPayload>(jsonData.ToString()!);
            if (chatPayload == null)
                return Fail("INVALID_DATA", "Chat payload cannot be null.");

            var localAddress = AccountData.GetSingleAccount(chatPayload.FromAddress);
            if (localAddress == null)
                return Fail("NOT_OWNER", "You must own the from address.", 403);

            if (Globals.DecShopData?.DecShop == null)
                return Fail("NO_SHOP_DATA", "DecShop data cannot be null. Connect to a shop first.");

            var messageLengthCheck = chatPayload.Message.ToLengthCheck(240);
            if (!messageLengthCheck)
                return Fail("VALIDATION_ERROR", "Message is too long. Please shorten to 240 characters.");

            var chatMessage = new Chat.ChatMessage
            {
                Id = RandomStringUtility.GetRandomString(10, true),
                FromAddress = localAddress.Address,
                Message = chatPayload.Message,
                ToAddress = Globals.DecShopData.DecShop.DecShopURL,
                MessageHash = chatPayload.Message.ToHash(),
                ShopURL = Globals.DecShopData.DecShop.DecShopURL,
                TimeStamp = TimeUtil.GetTime(),
                IsThirdParty = chatPayload.IsThirdParty,
            };

            chatMessage.Signature = SignatureService.CreateSignature(
                chatMessage.FromAddress + chatMessage.TimeStamp.ToString(),
                localAddress.GetPrivKey, localAddress.PublicKey);

            var chatMessageJson = JsonConvert.SerializeObject(chatMessage);

            Message message = new Message
            {
                Data = chatMessageJson,
                Type = MessageType.Chat,
                ComType = MessageComType.Chat
            };

            if (Globals.ChatMessageDict.TryGetValue(chatMessage.ShopURL, out var chatMessageList))
            {
                chatMessageList.Add(chatMessage);
                Globals.ChatMessageDict[chatMessage.ShopURL] = chatMessageList;
            }
            else
            {
                Globals.ChatMessageDict.TryAdd(chatMessage.ShopURL, new List<Chat.ChatMessage> { chatMessage });
            }

            _ = DSTClient.SendShopMessageFromClient(message, false);
            return Ok(new { Message = "Message sent.", MessageId = chatMessage.Id });
        }

        /// <summary>
        /// Send a chat message from a shop owner
        /// </summary>
        [HttpPost("chat/shop")]
        public IActionResult SendShopChatMessage([FromBody] object jsonData)
        {
            var chatPayload = JsonConvert.DeserializeObject<Chat.ChatPayload>(jsonData.ToString()!);
            if (chatPayload == null)
                return Fail("INVALID_DATA", "Chat payload cannot be null.");

            var localAddress = AccountData.GetSingleAccount(chatPayload.FromAddress);
            if (localAddress == null)
                return Fail("NOT_OWNER", "You must own the from address.", 403);

            var myDecShop = DecShop.GetMyDecShopInfo();
            if (myDecShop == null)
                return Fail("NO_SHOP", "DecShop data cannot be null.", 404);

            if (myDecShop.OwnerAddress != chatPayload.FromAddress)
                return Fail("NOT_SHOP_OWNER", "Only the shop owner may send messages back.", 403);

            var messageLengthCheck = chatPayload.Message.ToLengthCheck(240);
            if (!messageLengthCheck)
                return Fail("VALIDATION_ERROR", "Message is too long. Please shorten to 240 characters.");

            if (chatPayload.ToAddress == null)
                return Fail("VALIDATION_ERROR", "'To' address cannot be null.");

            if (!Globals.ShopChatUsers.TryGetValue(chatPayload.ToAddress, out var endpoint))
                return Fail("NOT_FOUND", "Shop endpoint was null. Ensure the user has sent a message and is actively communicating.", 404);

            var chatMessage = new Chat.ChatMessage
            {
                Id = RandomStringUtility.GetRandomString(10, true),
                FromAddress = localAddress.Address,
                Message = chatPayload.Message,
                ToAddress = chatPayload.ToAddress,
                MessageHash = chatPayload.Message.ToHash(),
                ShopURL = myDecShop.DecShopURL,
                TimeStamp = TimeUtil.GetTime(),
                IsShopSentMessage = true,
            };

            chatMessage.Signature = SignatureService.CreateSignature(
                chatMessage.FromAddress + chatMessage.TimeStamp.ToString(),
                localAddress.GetPrivKey, localAddress.PublicKey);

            var chatMessageJson = JsonConvert.SerializeObject(chatMessage);

            Message message = new Message
            {
                Data = chatMessageJson,
                Type = MessageType.Chat,
                ComType = MessageComType.Chat
            };

            if (Globals.ChatMessageDict.TryGetValue(chatPayload.ToAddress, out var chatMessageList))
            {
                chatMessageList.Add(chatMessage);
                Globals.ChatMessageDict[chatPayload.ToAddress] = chatMessageList;
            }
            else
            {
                Globals.ChatMessageDict.TryAdd(chatPayload.ToAddress, new List<Chat.ChatMessage> { chatMessage });
            }

            _ = DSTClient.SendClientMessageFromShop(message, endpoint, false);
            return Ok(new { Message = "Message sent." });
        }

        /// <summary>
        /// Resend a chat message
        /// </summary>
        [HttpPost("chat/{messageId}/resend")]
        public IActionResult ResendChatMessage(string messageId, [FromQuery] string shopUrl)
        {
            if (string.IsNullOrWhiteSpace(shopUrl))
                return Fail("VALIDATION_ERROR", "shopUrl query parameter is required.");

            if (!Globals.ChatMessageDict.TryGetValue(shopUrl, out var chatMessageList))
                return Fail("NOT_FOUND", "Chat messages were not found.", 404);

            var chatMessage = chatMessageList.Where(x => x.Id == messageId).FirstOrDefault();
            if (chatMessage == null)
                return Fail("NOT_FOUND", "Chat message was not found.", 404);

            if (chatMessage.MessageReceived)
                return Ok(new { Message = "Message was already reported as received." });

            var chatMessageJson = JsonConvert.SerializeObject(chatMessage);

            Message message = new Message
            {
                Data = chatMessageJson,
                Type = MessageType.Chat,
                ComType = MessageComType.Chat
            };

            _ = DSTClient.SendShopMessageFromClient(message, false);
            return Ok(new { Message = "Message resent." });
        }

        /// <summary>
        /// Get detailed chat messages for a shop URL
        /// </summary>
        [HttpGet("chat/messages")]
        public IActionResult GetChatMessages([FromQuery] string shopUrl)
        {
            if (string.IsNullOrWhiteSpace(shopUrl))
                return Fail("VALIDATION_ERROR", "shopUrl query parameter is required.");

            if (!Globals.ChatMessageDict.TryGetValue(shopUrl, out var chatMessageList) || chatMessageList.Count == 0)
                return Ok(Array.Empty<object>());

            return Ok(chatMessageList);
        }

        /// <summary>
        /// Get simplified chat messages for a shop URL
        /// </summary>
        [HttpGet("chat/messages/simple")]
        public IActionResult GetSimpleChatMessages([FromQuery] string shopUrl)
        {
            if (string.IsNullOrWhiteSpace(shopUrl))
                return Fail("VALIDATION_ERROR", "shopUrl query parameter is required.");

            if (!Globals.ChatMessageDict.TryGetValue(shopUrl, out var chatMessageList) || chatMessageList.Count == 0)
                return Ok(Array.Empty<object>());

            var simpleChatMessage = chatMessageList.Select(x => new
            {
                x.Id, x.Message, x.TimeStamp, x.FromAddress, x.ToAddress, x.IsShopSentMessage
            }).ToList();

            return Ok(simpleChatMessage);
        }

        /// <summary>
        /// Get a specific chat message by ID
        /// </summary>
        [HttpGet("chat/messages/{messageId}")]
        public IActionResult GetSpecificChatMessage(string messageId)
        {
            var specificMessage = Globals.ChatMessageDict.Values
                .SelectMany(x => x).Where(y => y.Id == messageId).FirstOrDefault();

            if (specificMessage == null)
                return Fail("NOT_FOUND", "Chat message was not found.", 404);

            return Ok(specificMessage);
        }

        /// <summary>
        /// Get most recent chat messages by key
        /// </summary>
        [HttpGet("chat/messages/recent/{key}")]
        public IActionResult GetRecentChatMessages(string key)
        {
            if (!Globals.ChatMessageDict.ContainsKey(key))
                return Ok(Array.Empty<object>());

            var chat = Globals.ChatMessageDict[key];
            if (chat == null)
                return Ok(Array.Empty<object>());

            var chatSummary = chat.OrderByDescending(x => x.TimeStamp).Take(50);
            return Ok(chatSummary);
        }

        /// <summary>
        /// Get chat message summary for shop owner
        /// </summary>
        [HttpGet("chat/messages/summary")]
        public IActionResult GetSummaryChatMessages()
        {
            var chatMessages = Globals.ChatMessageDict.Keys.ToList();
            if (chatMessages.Count == 0)
                return Ok(Array.Empty<object>());

            var sMessages = Globals.ChatMessageDict.Select(x => new
            {
                User = x.Key,
                Messages = x.Value.Count > 0 ? x.Value.OrderByDescending(y => y.TimeStamp).Take(1) : null
            }).ToList();

            return Ok(sMessages);
        }

        /// <summary>
        /// Delete chat messages by key
        /// </summary>
        [HttpDelete("chat/messages/{key}")]
        public IActionResult DeleteChatMessages(string key)
        {
            if (Globals.ChatMessageDict.TryRemove(key, out _))
                return Ok(new { Message = "Chat messages have been deleted." });

            return Fail("NOT_FOUND", "Chat messages not found for this key.", 404);
        }

        /// <summary>
        /// Get simple shop chat messages (for shop owner)
        /// </summary>
        [HttpGet("chat/shop-messages/simple")]
        public IActionResult GetSimpleShopChatMessages()
        {
            var chatMessages = Globals.ChatMessageDict.Keys.ToList();
            if (chatMessages.Count == 0)
                return Ok(Array.Empty<object>());

            var sMessages = Globals.ChatMessageDict.Select(x => new
            {
                User = x.Key,
                Messages = x.Value.Count > 0 ? x.Value.Select(y => new
                {
                    y.Id, y.Message, y.TimeStamp, y.FromAddress, y.ToAddress, y.IsShopSentMessage
                }) : null
            }).ToList();

            return Ok(sMessages);
        }

        /// <summary>
        /// Get detailed shop chat messages (for shop owner)
        /// </summary>
        [HttpGet("chat/shop-messages/detailed")]
        public IActionResult GetDetailedShopChatMessages()
        {
            var chatMessages = Globals.ChatMessageDict.Keys.ToList();
            if (chatMessages.Count == 0)
                return Ok(Array.Empty<object>());

            var sMessages = Globals.ChatMessageDict.Select(x => new
            {
                User = x.Key,
                Messages = x.Value.Count > 0 ? x.Value : null
            }).ToList();

            return Ok(sMessages);
        }

        /// <summary>
        /// Get detailed shop chat messages for a specific address
        /// </summary>
        [HttpGet("chat/shop-messages/{vfxAddress}")]
        public IActionResult GetDetailedSpecificShopChatMessages(string vfxAddress)
        {
            if (!Globals.ChatMessageDict.ContainsKey(vfxAddress))
                return Ok(Array.Empty<object>());

            var chat = Globals.ChatMessageDict[vfxAddress];
            return Ok(chat);
        }

        /// <summary>
        /// Get simple shop chat messages for a specific address
        /// </summary>
        [HttpGet("chat/shop-messages/{vfxAddress}/simple")]
        public IActionResult GetSimpleSpecificShopChatMessages(string vfxAddress)
        {
            if (!Globals.ChatMessageDict.ContainsKey(vfxAddress))
                return Ok(Array.Empty<object>());

            var chat = Globals.ChatMessageDict[vfxAddress];
            var chatSimple = chat.Select(x => new
            {
                x.Message, x.TimeStamp, x.FromAddress, x.ToAddress, x.IsShopSentMessage
            });

            return Ok(chatSimple);
        }

        #endregion

        #region NFT Purchase

        /// <summary>
        /// Complete an NFT purchase
        /// </summary>
        [HttpPost("purchases/complete")]
        public async Task<IActionResult> CompleteNFTPurchase([FromBody] CompleteNftPurchaseRequest request)
        {
            var scStateTrei = SmartContractStateTrei.GetSmartContractState(request.ScUID);
            if (scStateTrei == null)
                return Fail("NOT_FOUND", "Smart contract was not found.", 404);

            var nextOwner = scStateTrei.NextOwner;
            var purchaseAmount = scStateTrei.PurchaseAmount;

            if (nextOwner == null || purchaseAmount == null)
                return Fail("INVALID_STATE", $"Smart contract data missing or purchase already completed. Next Owner: {nextOwner} | Purchase Amount: {purchaseAmount}");

            var localAccount = AccountData.GetSingleAccount(nextOwner);
            if (localAccount == null)
                return Fail("NOT_OWNER", $"A local account with next owner address was not found. Next Owner: {nextOwner}", 403);

            if (localAccount.Balance <= purchaseAmount.Value)
                return Fail("INSUFFICIENT_FUNDS", $"Not enough funds. Purchase Amount: {purchaseAmount} | Balance: {localAccount.Balance}");

            var result = await SmartContractService.CompleteSaleSmartContractTX(
                request.ScUID, scStateTrei.OwnerAddress, purchaseAmount.Value, request.KeySign);

            if (result.Item1 == null)
                return Fail("PURCHASE_FAILED", result.Item2);

            return Ok(new { TxHash = result.Item1.Hash, Message = result.Item2 });
        }

        #endregion

        #region Debug

        /// <summary>
        /// Get DST debug data
        /// </summary>
        [HttpGet("debug")]
        public IActionResult GetDebug()
        {
            return Ok(new
            {
                Clients = Globals.ConnectedClients,
                Shops = Globals.ConnectedShops,
                StunServer = Globals.STUNServer
            });
        }

        /// <summary>
        /// Get shop statistics
        /// </summary>
        [HttpGet("debug/data")]
        public IActionResult GetDebugData()
        {
            return Ok(new
            {
                CollectionCount = Collection.GetLiveCollectionCount(),
                ListingCount = Listing.GetLiveListingsCount(),
                AuctionCount = Auction.GetLiveAuctionsCount()
            });
        }

        #endregion
    }
}
