using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;
using System.Runtime.CompilerServices;

namespace ReserveBlockCore.Models
{
    public class Beacons
    {
        public int Id { get; set; }
        public string IPAddress { get; set; }
        public int Port { get; set; }
        public string Name { get; set; }
        public string BeaconUID { get; set; }
        public bool DefaultBeacon { get; set; }
        public bool AutoDeleteAfterDownload { get; set; }
        public bool IsPrivateBeacon { get; set; }
        public int FileCachePeriodDays { get; set; }
        public string BeaconLocator { get; set; }
        public bool SelfBeacon { get; set; }
        public bool SelfBeaconActive { get; set; }
        public int Region { get; set; }

        public static LiteDB.ILiteCollection<Beacons>? GetBeacons()
        {
            try
            {
                var beacons = DbContext.DB_Beacon.GetCollection<Beacons>(DbContext.RSRV_BEACONS);
                return beacons;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "Beacons.GetBeacons()");
                return null;
            }
        }

        public static void CreateDefaultBeaconFile()
        {
            var path = GetPathUtility.GetConfigPath();
            var fileExist = File.Exists(path + "beacons.txt");

            if (!fileExist)
            {
                List<Beacons> beaconList = new List<Beacons>
                {
                    new Beacons { IPAddress = "144.126.156.180", Name = "Lily Beacon V2", Port = Globals.Port + 1 + 20000, BeaconUID = "LilyBeaconV2", DefaultBeacon = true, AutoDeleteAfterDownload = true, FileCachePeriodDays = 2, IsPrivateBeacon = false, SelfBeacon = false, SelfBeaconActive = false, BeaconLocator = "", Region = 1 },
                    new Beacons { IPAddress = "144.126.156.176", Name = "Wisteria Beacon V2", Port = Globals.Port + 1 + 20000, BeaconUID = "WisteriaBeaconV2", DefaultBeacon = true, AutoDeleteAfterDownload = true, FileCachePeriodDays = 2, IsPrivateBeacon = false, SelfBeacon = false, SelfBeaconActive = false, BeaconLocator = "", Region = 1 },
                    new Beacons { IPAddress = "144.126.141.210", Name = "Tulip Beacon V2", Port = Globals.Port + 1 + 20000, BeaconUID = "TulipBeaconV2", DefaultBeacon = true, AutoDeleteAfterDownload = true, FileCachePeriodDays = 2, IsPrivateBeacon = false, SelfBeacon = false, SelfBeaconActive = false, BeaconLocator = "", Region = 1 },
                    new Beacons { IPAddress = "144.126.141.56", Name = "Sunflower Beacon V2", Port = Globals.Port + 1 + 20000, BeaconUID = "SunflowerBeaconV2", DefaultBeacon = true, AutoDeleteAfterDownload = true, FileCachePeriodDays = 2, IsPrivateBeacon = false, SelfBeacon = false, SelfBeaconActive = false, BeaconLocator = "", Region = 1 },
                    new Beacons { IPAddress = "135.148.121.99", Name = "Lavender Beacon V2", Port = Globals.Port + 1 + 20000, BeaconUID = "LavenderBeaconV2", DefaultBeacon = true, AutoDeleteAfterDownload = true, FileCachePeriodDays = 2, IsPrivateBeacon = false, SelfBeacon = false, SelfBeaconActive = false, BeaconLocator = "", Region = 1}
                };

                var lines = new List<string>();

                foreach (var b in beaconList)
                {
                    var line = string.Join(",",
                        b.IPAddress,
                        b.Name.Replace(",", " "),
                        b.Port,
                        b.BeaconUID,
                        b.DefaultBeacon,
                        b.AutoDeleteAfterDownload,
                        b.FileCachePeriodDays,
                        b.IsPrivateBeacon,
                        b.SelfBeacon,
                        b.SelfBeaconActive,
                        b.BeaconLocator.Replace(",", " "),
                        b.Region
                    );

                    lines.Add(line);
                }

                File.WriteAllLines(path + "beacons.txt", lines);
            }
        }

        public static List<Beacons> ReadBeaconFile()
        {
            var path = GetPathUtility.GetConfigPath();
            var filePath = Path.Combine(path, "beacons.txt");

            var beacons = new List<Beacons>();

            if (!File.Exists(filePath))
                return beacons;

            var lines = File.ReadAllLines(filePath);

            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');

                if (parts.Length < 12)
                    continue; // skip malformed lines

                try
                {
                    var beacon = new Beacons
                    {
                        IPAddress = parts[0],
                        Name = parts[1],
                        Port = int.Parse(parts[2]),
                        BeaconUID = parts[3],
                        DefaultBeacon = bool.Parse(parts[4]),
                        AutoDeleteAfterDownload = bool.Parse(parts[5]),
                        FileCachePeriodDays = int.Parse(parts[6]),
                        IsPrivateBeacon = bool.Parse(parts[7]),
                        SelfBeacon = bool.Parse(parts[8]),
                        SelfBeaconActive = bool.Parse(parts[9]),
                        BeaconLocator = parts[10],
                        Region = int.Parse(parts[11])
                    };

                    beacons.Add(beacon);
                }
                catch
                {
                    ErrorLogUtility.LogError($"Failed to read line in beacon file at line:  {i + 1}", "BeaconInfo.ReadBeaconFile()");
                }
            }

            return beacons;
        }

        public static bool SaveBeacon(Beacons beacon)
        {
            try
            {
                var beacons = GetBeacons();
                if (beacons == null)
                {
                    ErrorLogUtility.LogError("GetBeacon() returned a null value.", "BeaconInfo.SaveBeaconInfo()");
                }
                else
                {
                    var existingBeaconInfo = beacons.Query().Where(x => x.IPAddress == beacon.IPAddress).FirstOrDefault();
                    if (existingBeaconInfo == null)
                    {
                        beacons.InsertSafe(beacon); //inserts new record
                        return true;
                    }
                    else
                    {
                        existingBeaconInfo.Name = beacon.Name;
                        existingBeaconInfo.Port = beacon.Port;
                        existingBeaconInfo.SelfBeacon = beacon.SelfBeacon;
                        existingBeaconInfo.SelfBeaconActive = beacon.SelfBeaconActive;
                        existingBeaconInfo.BeaconLocator = beacon.BeaconLocator;
                        existingBeaconInfo.AutoDeleteAfterDownload = beacon.AutoDeleteAfterDownload;
                        existingBeaconInfo.FileCachePeriodDays = beacon.FileCachePeriodDays;
                        existingBeaconInfo.IsPrivateBeacon = beacon.IsPrivateBeacon;
                        existingBeaconInfo.BeaconUID = beacon.BeaconUID;
                        existingBeaconInfo.Region = beacon.Region;

                        beacons.UpdateSafe(existingBeaconInfo); //update existing record
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void SaveBeaconList(List<Beacons> beaconList)
        {
            var beacons = GetBeacons();
            if (beacons != null)
            {
                if(beaconList.Count() > 0)
                {
                    foreach(var beacon in beaconList) 
                    {
                        var existingBeaconInfo = beacons.Query().Where(x => x.IPAddress == beacon.IPAddress).FirstOrDefault();
                        if (existingBeaconInfo == null)
                        {
                            beacons.InsertSafe(beacon); //inserts new record
                            
                        }
                        else
                        {
                            existingBeaconInfo.Name = beacon.Name;
                            existingBeaconInfo.Port = beacon.Port;
                            //existingBeaconInfo.SelfBeacon = beacon.SelfBeacon;
                            //existingBeaconInfo.SelfBeaconActive = beacon.SelfBeaconActive;
                            existingBeaconInfo.BeaconLocator = beacon.BeaconLocator;
                            existingBeaconInfo.AutoDeleteAfterDownload = beacon.AutoDeleteAfterDownload;
                            existingBeaconInfo.FileCachePeriodDays = beacon.FileCachePeriodDays;
                            existingBeaconInfo.IsPrivateBeacon = beacon.IsPrivateBeacon;
                            existingBeaconInfo.BeaconUID = beacon.BeaconUID;
                            existingBeaconInfo.DefaultBeacon = beacon.DefaultBeacon;
                            existingBeaconInfo.Region = beacon.Region;

                            beacons.UpdateSafe(existingBeaconInfo); //update existing record
                        }
                    }
                }
            }
            else
            {
                ErrorLogUtility.LogError("GetBeacon() returned a null value.", "BeaconInfo.SaveBeaconInfo()");
            }
        }

        public static string CreateBeaconLocator(Beacons beacon)
        {
            BeaconInfo.BeaconInfoJson beaconLoc = new BeaconInfo.BeaconInfoJson { 
                IPAddress = beacon.IPAddress, 
                Port = beacon.Port, 
                Name = beacon.Name, 
                BeaconUID = beacon.BeaconUID
            };
            var beaconLocJson = JsonConvert.SerializeObject(beaconLoc);

            var beaconLocJsonBase64 = beaconLocJson.ToBase64();

            return beaconLocJsonBase64;
        }

        public static bool DeleteBeacon(Beacons beacon)
        {
            var beacons = GetBeacons();
            if (beacons == null)
            {
                ErrorLogUtility.LogError("GetBeacons() returned a null value.", "BeaconInfo.SaveBeaconInfo()");
            }
            else
            {
                var existingBeaconInfo = beacons.Query().Where(x => x.IPAddress == beacon.IPAddress).FirstOrDefault();
                if (existingBeaconInfo != null)
                {
                    beacons.Delete(existingBeaconInfo.Id);
                    return true;
                }
            }

            return false;
        }

        public static bool? SetBeaconActiveState()
        {
            var beacons = GetBeacons();
            if (beacons == null)
            {
                ErrorLogUtility.LogError("GetBeacon() returned a null value.", "BeaconInfo.SaveBeaconInfo()");
            }
            else
            {
                var beaconInfo = beacons.Query().Where(x => x.SelfBeacon == true).FirstOrDefault();
                if (beaconInfo == null)
                {
                    return null;
                }
                else
                {
                    beaconInfo.SelfBeaconActive = !beaconInfo.SelfBeaconActive;
                    beacons.UpdateSafe(beaconInfo);
                    if(Globals.SelfBeacon != null)
                        Globals.SelfBeacon.SelfBeaconActive = beaconInfo.SelfBeaconActive;
                    return beaconInfo.SelfBeaconActive;
                }
            }

            return null;
        }
    }
}
