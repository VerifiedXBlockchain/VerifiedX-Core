﻿using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ReserveBlockCore.Utilities
{
    public class GetPathUtility
    {
        private static string MainFolder = Globals.IsTestNet != true ? "RBX" : "RBXTest";

        public static string IPFromURL(string url)
        {
            var matches = Regex.Matches(url, @"[.:\d]+").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value).ToArray();
            var ipPart = matches.Where(x => x.Split('.').Length == 4).FirstOrDefault();
            return ipPart.Split(':')[0];
        }

        public static string GetDatabasePath()
        {
            string path = "";

            var databaseLocation = Globals.IsTestNet != true ? "Databases" : "DatabasesTestNet";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + MainFolder.ToLower() + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + MainFolder + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + MainFolder + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        public static string GetCheckpointPath()
        {
            string path = "";

            var checkpointLocation = Globals.IsTestNet != true ? "Checkpoint" : "CheckpointTestNet";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + MainFolder.ToLower() + Path.DirectorySeparatorChar + checkpointLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + checkpointLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + MainFolder + Path.DirectorySeparatorChar + checkpointLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + MainFolder + Path.DirectorySeparatorChar + checkpointLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        public static string GetBeaconPath()
        {
            string path = "";

            var beaconLocation = Globals.IsTestNet != true ? "BeaconAsset" : "BeaconAssetTestNet";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + MainFolder.ToLower() + Path.DirectorySeparatorChar + beaconLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + beaconLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + MainFolder + Path.DirectorySeparatorChar + beaconLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + MainFolder + Path.DirectorySeparatorChar + beaconLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        public static string GetConfigPath()
        {
            string path = "";

            var configLocation = Globals.IsTestNet != true ? "Config" : "ConfigTestNet";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + MainFolder.ToLower() + Path.DirectorySeparatorChar + configLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + configLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + MainFolder + Path.DirectorySeparatorChar + configLocation + Path.DirectorySeparatorChar;
                }
            }
            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + MainFolder + Path.DirectorySeparatorChar + configLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        public static string GetABLPath()
        {
            string path = "";

            var ablLocation = Globals.IsTestNet != true ? "Config" : "ConfigTestNet";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + MainFolder.ToLower() + Path.DirectorySeparatorChar + ablLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + ablLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + MainFolder + Path.DirectorySeparatorChar + ablLocation + Path.DirectorySeparatorChar;
                }
            }
            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + MainFolder + Path.DirectorySeparatorChar + ablLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }


        public static string GetTrilliumPath()
        {
            var trilliumLocation = Globals.IsTestNet != true ? "Trillium" : "TrilliumTestNet";

            string path = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + MainFolder.ToLower() + Path.DirectorySeparatorChar +
                    trilliumLocation + Path.DirectorySeparatorChar + "Submissions" + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + trilliumLocation + Path.DirectorySeparatorChar +
                        "Submissions" + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + MainFolder + Path.DirectorySeparatorChar + trilliumLocation + Path.DirectorySeparatorChar +
                        "Submissions" + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + "RBX" + Path.DirectorySeparatorChar + trilliumLocation + Path.DirectorySeparatorChar + "Submissions" + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }
    }
}
