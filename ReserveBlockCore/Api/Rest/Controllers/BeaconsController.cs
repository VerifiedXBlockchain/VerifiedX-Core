using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class BeaconsController : RestBaseController
    {
        /// <summary>
        /// List all beacons
        /// </summary>
        [HttpGet]
        public IActionResult GetAll()
        {
            var beacons = Beacons.GetBeacons();
            if (beacons == null)
                return Ok(Array.Empty<object>());

            var beaconList = beacons.Query().Where(x => true).ToEnumerable().ToList();
            if (beaconList.Count == 0)
                return Ok(Array.Empty<object>());

            return Ok(beaconList);
        }

        /// <summary>
        /// Create a local beacon
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateBeaconRequest request)
        {
            var ip = P2PClient.MostLikelyIP();
            if (ip == "NA")
                return Fail("NO_IP", "Could not get external IP. Please ensure you are connected to peers and ports are not blocked.");

            var bUID = Guid.NewGuid().ToString().Substring(0, 12).Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

            var beaconLoc = new BeaconInfo.BeaconInfoJson
            {
                IPAddress = ip,
                Port = request.Port != 0 ? request.Port : Globals.Port + 20000 + 1,
                Name = request.Name,
                BeaconUID = bUID
            };

            var beaconLocJson = JsonConvert.SerializeObject(beaconLoc);

            var beacon = new Beacons
            {
                IPAddress = ip,
                Name = request.Name,
                Port = request.Port != 0 ? request.Port : Globals.Port + 20000 + 1,
                BeaconUID = bUID,
                DefaultBeacon = false,
                AutoDeleteAfterDownload = request.AutoDelete,
                FileCachePeriodDays = request.FileCachePeriod,
                IsPrivateBeacon = request.IsPrivate,
                SelfBeacon = true,
                SelfBeaconActive = true,
                BeaconLocator = beaconLocJson.ToBase64(),
                Region = 0
            };

            var result = Beacons.SaveBeacon(beacon);
            if (!result)
                return Fail("CREATE_FAILED", "Failed to add beacon.");

            await StartupService.SetSelfBeacon();
            Globals.Beacons[beacon.IPAddress] = beacon;

            return Created(beacon);
        }

        /// <summary>
        /// Add a remote beacon
        /// </summary>
        [HttpPost("add")]
        public IActionResult AddRemote([FromBody] AddBeaconRequest request)
        {
            var bUID = Guid.NewGuid().ToString().Substring(0, 12).Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

            var beacon = new Beacons
            {
                IPAddress = request.IpAddress,
                Name = request.Name,
                Port = request.Port == 0 ? Globals.Port + 20000 + 1 : request.Port,
                BeaconUID = bUID,
                DefaultBeacon = false,
                AutoDeleteAfterDownload = false,
                FileCachePeriodDays = 0,
                IsPrivateBeacon = false,
                SelfBeacon = false,
                SelfBeaconActive = false,
                Region = 0
            };

            beacon.BeaconLocator = Beacons.CreateBeaconLocator(beacon);

            var result = Beacons.SaveBeacon(beacon);
            if (!result)
                return Fail("ADD_FAILED", "Failed to add beacon.");

            Globals.Beacons[beacon.IPAddress] = beacon;

            return Created(beacon);
        }

        /// <summary>
        /// Delete a beacon by ID
        /// </summary>
        [HttpDelete("{id:int}")]
        public IActionResult Delete(int id)
        {
            var beacons = Beacons.GetBeacons();
            if (beacons == null)
                return Fail("NOT_FOUND", "No beacons database found.", 404);

            var beacon = beacons.Query().Where(x => x.Id == id).FirstOrDefault();
            if (beacon == null)
                return Fail("NOT_FOUND", "Beacon does not exist.", 404);

            var result = Beacons.DeleteBeacon(beacon);
            if (!result)
                return Fail("DELETE_FAILED", "Failed to delete beacon.");

            Globals.Beacons.TryRemove(beacon.IPAddress, out _);

            return Ok("Beacon has been deleted.");
        }

        /// <summary>
        /// Get local beacon info
        /// </summary>
        [HttpGet("info")]
        public IActionResult GetInfo()
        {
            var beacon = Globals.SelfBeacon;
            if (beacon == null)
                return Fail("NOT_FOUND", "No local beacon found.", 404);

            return Ok(beacon);
        }

        /// <summary>
        /// Toggle beacon active state
        /// </summary>
        [HttpPost("toggle")]
        public IActionResult Toggle()
        {
            var result = Beacons.SetBeaconActiveState();
            if (result == null)
                return Fail("TOGGLE_FAILED", "Error turning beacon on/off.");

            return Ok(new { Active = result.Value });
        }

        /// <summary>
        /// Get asset queue
        /// </summary>
        [HttpGet("assets/queue")]
        public IActionResult GetAssetQueue()
        {
            var aqDB = AssetQueue.GetAssetQueue();
            if (aqDB == null)
                return Ok(Array.Empty<object>());

            var aqList = aqDB.Query().Where(x => true).ToEnumerable().ToList();
            if (aqList.Count == 0)
                return Ok(Array.Empty<object>());

            return Ok(aqList);
        }
    }
}
