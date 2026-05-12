using ElmahCore;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ReserveBlockCore.Utilities
{
    public class LogUtility
    {
        private static ConcurrentQueue<(string Message, string Location, string FileName, DateTime Time)> FileQueue = new ConcurrentQueue<(string, string, string, DateTime)>();
        public static void LogQueue(string message, string location, string fileName, bool log)
        {
            if(!log)
                return; // this disables the log queue
            FileQueue.Enqueue((message, location, fileName, DateTime.Now));
        }

        private static async Task LogEnqueue(string message, string location, string fileName, bool log)
        {
            if (!log)
                return; // this disables the log queue
            FileQueue.Enqueue((message, location, fileName, DateTime.Now));
        }

        public static async Task LogLoop()
        {
            var databaseLocation = Globals.IsTestNet != true ? "Databases" : "DatabasesTestNet";
            var mainFolderPath = Globals.IsTestNet != true ? "RBX" : "RBXTest";
            
            string path = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + mainFolderPath.ToLower() + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            while (true)
            {
                while(FileQueue.Count > 0)
                {
                    if(FileQueue.TryDequeue(out var content))
                    {
                        try
                        {
                            var text = "[" + content.Time + "]" + " : " + "[" + content.Location + "]" + " : " + content.Message;
                            await File.AppendAllTextAsync(path + content.FileName, Environment.NewLine + text);
                        }
                        catch (Exception ex)
                        {
                            // Don't let a single write failure kill the entire log loop
                            Console.WriteLine($"[LogUtility.LogLoop] Failed to write log entry: {ex.Message}");
                        }
                    }
                }

                await Task.Delay(20);
            }
        }
        public static async void Log(string message, string location, bool firstEntry = false)
        {
            try
            {
                var databaseLocation = Globals.IsTestNet != true ? "Databases" : "DatabasesTestNet";
                var mainFolderPath = Globals.IsTestNet != true ? "RBX" : "RBXTest";

                var locationMessage = "[" + DateTime.Now.ToString() + "]" + " : " + "[" + location + "]";
                var text = "[" + DateTime.Now.ToString() +  "]" + " : " + "[" + location + "]" + " : " + message;
                string path = "";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    path = homeDirectory + Path.DirectorySeparatorChar + mainFolderPath.ToLower() + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    if (Debugger.IsAttached)
                    {
                        path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                    }
                    else
                    {
                        path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                    }
                }

                if (!string.IsNullOrEmpty(Globals.CustomPath))
                {
                    path = Globals.CustomPath + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }

                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                if (firstEntry == true)
                {
                    await File.AppendAllTextAsync(path + "rbxlog.txt", Environment.NewLine + " ");
                }

                await LogEnqueue(message, location, "rbxlog.txt", true);
                //await File.AppendAllTextAsync(path + "rbxlog.txt", Environment.NewLine + text);
                VFXLogging.LogInfo(message, location);
            }
            catch (Exception ex)
            {
                try
                {
                    // Synchronous fallback — async void swallows exceptions silently,
                    // so we must not let the catch block itself rely on async infrastructure.
                    var fallbackText = $"[{DateTime.Now}] [LogUtility.Log] Error Logging: {ex}{Environment.NewLine}Original message: [{location}] {message}";
                    Console.WriteLine(fallbackText);
                    LogEnqueue($"Error Logging: {ex}", "", "rbxlog.txt", true).ConfigureAwait(false);
                }
                catch
                {
                    // Last resort — prevent async void from crashing the process
                }
            }
        }

        public static async Task ClearLog()
        {
            var databaseLocation = Globals.IsTestNet != true ? "Databases" : "DatabasesTestNet";
            var mainFolderPath = Globals.IsTestNet != true ? "RBX" : "RBXTest";

            string path = "";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + mainFolderPath.ToLower() + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            await File.WriteAllTextAsync(path + "rbxlog.txt", "");
        }

        public static async Task<string> ReadLog()
        {
            var databaseLocation = Globals.IsTestNet != true ? "Databases" : "DatabasesTestNet";
            var mainFolderPath = Globals.IsTestNet != true ? "RBX" : "RBXTest";

            string path = "";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + mainFolderPath.ToLower() + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + mainFolderPath + Path.DirectorySeparatorChar + databaseLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var result = await File.ReadAllLinesAsync(path + "rbxlog.txt");

            StringBuilder strBld = new StringBuilder();

            foreach (var line in result)
            {
                strBld.AppendLine(line);
            }

            return strBld.ToString();
        }
    }
}
