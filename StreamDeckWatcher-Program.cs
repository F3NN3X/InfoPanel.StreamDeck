using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace StreamDeckWatcher
{
    class Program
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool bWatchSubtree, uint dwNotifyFilter, IntPtr hEvent, bool fAsynchronous);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegCloseKey(IntPtr hKey);

        private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
        private const int KEY_QUERY_VALUE = 0x0001;
        private const int KEY_NOTIFY = 0x0010;
        private const int KEY_READ = 0x20019;
        private static readonly IntPtr HKEY_CURRENT_USER = new IntPtr(-2147483647);

        private static readonly Dictionary<string, DeviceState> Devices = new Dictionary<string, DeviceState>();
        private static readonly Dictionary<string, string> ProfileNameCache = new Dictionary<string, string>();
        private static readonly string ProfilesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "ProfilesV2");

        class DeviceState
        {
            public string Serial { get; set; } = "";
            public string FullId { get; set; } = "";
            public string DeviceName { get; set; } = "";
            public string ProfileUuid { get; set; } = "";
            public string ProfileName { get; set; } = "";
            public DateTime LastUpdate { get; set; }
        }

        private static readonly Dictionary<string, string> IdToCustomName = new Dictionary<string, string>();
        private static readonly Dictionary<string, HashSet<string>> IdToProfileNames = new Dictionary<string, HashSet<string>>();
        private static DateTime LastLogScan = DateTime.MinValue;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Stream Deck Profile Watcher (Registry + Log Mode) ===");
            Console.WriteLine("Monitoring active profiles via Windows Registry and Logs...");
            Console.WriteLine("Press 'q' to quit.");

            var cts = new CancellationTokenSource();
            var task = Task.Run(() => MonitorLoop(cts.Token));

            while (Console.Read() != 'q') ;
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }
        }

        private static async Task MonitorLoop(CancellationToken token)
        {
            IntPtr hKey = IntPtr.Zero;
            try
            {
                // Open the key for monitoring
                int result = RegOpenKeyEx(HKEY_CURRENT_USER, @"Software\Elgato Systems GmbH\StreamDeck", 0, KEY_READ, out hKey);
                if (result != 0)
                {
                    Console.WriteLine($"Error opening registry key: {result}. Registry monitoring aborted. Please ensure Stream Deck software is installed.");
                    return;
                }

                using (var regChangedEvent = new AutoResetEvent(false))
                {
                    // Initial Scan
                    PollRegistry();
                    ScanLogs();
                    ResolveCustomNames();
                    DisplayStatus();
                    LastLogScan = DateTime.Now;

                    bool isWatching = false;

                    while (!token.IsCancellationRequested)
                    {
                        if (!isWatching)
                        {
                            // Request notification for value changes
                            RegNotifyChangeKeyValue(hKey, true, REG_NOTIFY_CHANGE_LAST_SET, regChangedEvent.SafeWaitHandle.DangerousGetHandle(), true);
                            isWatching = true;
                        }

                        // Wait for registry change or timeout (1s)
                        // We use a timeout so we can check the cancellation token periodically
                        bool changed = regChangedEvent.WaitOne(1000);

                        if (changed)
                        {
                            isWatching = false; // Event fired, need to re-register watch
                            PollRegistry();
                            ResolveCustomNames();
                            DisplayStatus();
                        }

                        // Periodic Log Scan (every 10s)
                        if ((DateTime.Now - LastLogScan).TotalSeconds > 10)
                        {
                            ScanLogs();
                            ResolveCustomNames();
                            DisplayStatus();
                            LastLogScan = DateTime.Now;
                        }
                    }
                }
            }
            finally
            {
                if (hKey != IntPtr.Zero) RegCloseKey(hKey);
            }
        }

        private static void ScanLogs()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string pluginsDir = Path.Combine(appData, "Elgato", "StreamDeck", "Plugins");

                if (!Directory.Exists(pluginsDir)) return;

                // 1. Scan for RegistrationParameters (ID -> Custom Name)
                // Look in all log files in all plugins
                foreach (var file in Directory.GetFiles(pluginsDir, "*.log", SearchOption.AllDirectories))
                {
                    // Optimization: Only read recent files or small files? 
                    // For now, just read the last few KB if possible, but RegistrationParameters is usually at the start.
                    // So we might need to read the whole file or the beginning.
                    // Actually, RegistrationParameters is logged when the plugin starts.

                    try
                    {
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                        {
                            string content = sr.ReadToEnd(); // Warning: could be large. 
                                                             // Better to read line by line or search.

                            // Regex for RegistrationParameters
                            // "devices":[{"id":"...","name":"...",...}]
                            var match = Regex.Match(content, @"devices"":\[(.*?)\]");
                            if (match.Success)
                            {
                                string devicesJson = match.Groups[1].Value;
                                var deviceMatches = Regex.Matches(devicesJson, @"\{""id"":""([a-f0-9]{32})""[^}]*?""name"":""([^""]+)""");
                                foreach (Match dm in deviceMatches)
                                {
                                    string id = dm.Groups[1].Value;
                                    string name = dm.Groups[2].Value;
                                    if (!IdToCustomName.ContainsKey(id))
                                    {
                                        IdToCustomName[id] = name;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 2. Scan for Profile Associations (ID -> Profile Name)
                // Specifically in com.example.streamdeck.profilemonitor.sdPlugin logs
                string monitorPluginDir = Path.Combine(pluginsDir, "com.example.streamdeck.profilemonitor.sdPlugin");
                if (Directory.Exists(monitorPluginDir))
                {
                    foreach (var file in Directory.GetFiles(monitorPluginDir, "*.log", SearchOption.AllDirectories))
                    {
                        try
                        {
                            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var sr = new StreamReader(fs))
                            {
                                string line;
                                while ((line = sr.ReadLine()) != null)
                                {
                                    // Format: Action initialized on Device: Model (ID) - Profile: Name
                                    var match = Regex.Match(line, @"Device: \w+ \(([a-f0-9]{32})\) - Profile: (.+)");
                                    if (match.Success)
                                    {
                                        string id = match.Groups[1].Value;
                                        string profile = match.Groups[2].Value.Trim();
                                        if (!IdToProfileNames.ContainsKey(id))
                                        {
                                            IdToProfileNames[id] = new HashSet<string>();
                                        }
                                        IdToProfileNames[id].Add(profile);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void ResolveCustomNames()
        {
            // 1. Direct Match via Profile Name
            foreach (var device in Devices.Values)
            {
                if (string.IsNullOrEmpty(device.ProfileName)) continue;

                foreach (var kvp in IdToProfileNames)
                {
                    if (kvp.Value.Contains(device.ProfileName))
                    {
                        if (IdToCustomName.TryGetValue(kvp.Key, out string customName))
                        {
                            device.DeviceName = customName;
                        }
                    }
                }
            }

            // 2. Process of Elimination / Inference
            // Identify devices that have names matching known custom names
            var knownCustomNames = IdToCustomName.Values.ToHashSet();

            // Devices that don't have a known custom name assigned yet
            var unmappedDevices = Devices.Values.Where(d => !knownCustomNames.Contains(d.DeviceName)).ToList();

            // Custom names that have already been assigned to a device
            var usedCustomNames = Devices.Values.Select(d => d.DeviceName).Where(n => knownCustomNames.Contains(n)).ToHashSet();

            // Custom names that are available (not yet assigned)
            var availableCustomNames = IdToCustomName.Values.Where(n => !usedCustomNames.Contains(n)).ToList();

            // If we have exactly one unmapped device and one available custom name, map them
            if (unmappedDevices.Count == 1 && availableCustomNames.Count == 1)
            {
                unmappedDevices[0].DeviceName = availableCustomNames[0];
            }
        }

        private static void PollRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Elgato Systems GmbH\StreamDeck"))
                {
                    if (key == null) return;

                    var bytes = key.GetValue("Devices") as byte[];
                    if (bytes == null) return;

                    string raw = Encoding.Unicode.GetString(bytes);
                    string clean = raw.Replace("\0", "");

                    var pattern = @"ESDProfilesPreferred[\s\S]*?([a-fA-F0-9-]{36})[\s\S]*?(@\(1\)\[\d+/\d+/(.*?)\])";
                    var matches = Regex.Matches(clean, pattern);

                    foreach (Match match in matches)
                    {
                        string uuid = match.Groups[1].Value;
                        string fullId = match.Groups[2].Value;
                        string serial = match.Groups[3].Value;

                        if (!Devices.ContainsKey(serial))
                        {
                            Devices[serial] = new DeviceState { Serial = serial, FullId = fullId };
                        }

                        var device = Devices[serial];

                        // Try to find Device Name if missing
                        if (string.IsNullOrEmpty(device.DeviceName))
                        {
                            // Look for "DeviceName" followed by text near this DeviceID
                            int idIndex = match.Groups[2].Index;
                            string context = clean.Substring(idIndex, Math.Min(500, clean.Length - idIndex));
                            // Regex: DeviceName followed by some non-word chars (control chars), then the name
                            var nameMatch = Regex.Match(context, @"DeviceName[\W_]*([A-Za-z0-9 ]+)");
                            if (nameMatch.Success)
                            {
                                device.DeviceName = nameMatch.Groups[1].Value.Trim();
                            }
                            else
                            {
                                device.DeviceName = "Stream Deck";
                            }
                        }

                        if (device.ProfileUuid != uuid)
                        {
                            device.ProfileUuid = uuid;
                            device.ProfileName = GetProfileName(uuid);
                            device.LastUpdate = DateTime.Now;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Silently fail
            }
        }

        private static string GetProfileName(string uuid)
        {
            if (ProfileNameCache.ContainsKey(uuid)) return ProfileNameCache[uuid];

            string manifestPath = Path.Combine(ProfilesDir, $"{uuid}.sdProfile", "manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    string json = File.ReadAllText(manifestPath);
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("Name", out var nameProp))
                        {
                            string name = nameProp.GetString() ?? "Unknown";
                            ProfileNameCache[uuid] = name;
                            return name;
                        }
                    }
                }
                catch { }
            }

            return "Unknown Profile";
        }

        private static void DisplayStatus()
        {
            if (!Console.IsOutputRedirected)
            {
                try { Console.Clear(); } catch { }
            }
            Console.WriteLine("=== Stream Deck Profile Watcher (Event-Based + Log Mode) ===");
            Console.WriteLine($"Last Update: {DateTime.Now:HH:mm:ss}");

            Console.WriteLine("------------------------------------------------------------------------------------------");
            Console.WriteLine($"{"Device Name",-20} | {"Serial",-15} | {"Profile Name",-20} | {"UUID",-36}");
            Console.WriteLine("------------------------------------------------------------------------------------------");

            foreach (var device in Devices.Values.OrderBy(d => d.Serial))
            {
                Console.WriteLine($"{device.DeviceName,-20} | {device.Serial,-15} | {device.ProfileName,-20} | {device.ProfileUuid}");
            }
        }
    }
}