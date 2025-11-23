using InfoPanel.StreamDeck.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.StreamDeck.Services
{
    public class DataUpdatedEventArgs : EventArgs
    {
        public StreamDeckData Data { get; }

        public DataUpdatedEventArgs(StreamDeckData data)
        {
            Data = data;
        }
    }

    public class MonitoringService : IDisposable
    {
        #region P/Invoke
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
        #endregion

        public event EventHandler<DataUpdatedEventArgs>? DataUpdated;

        private readonly ConfigurationService _configService;
        private readonly FileLoggingService _logger;
        private volatile bool _isMonitoring;
        private readonly object _lockObject = new();
        private Task? _monitoringTask;
        private CancellationTokenSource? _cts;

        // State
        private readonly Dictionary<string, DeviceState> _devices = new Dictionary<string, DeviceState>();
        private readonly Dictionary<string, string> _profileNameCache = new Dictionary<string, string>();
        private readonly string _profilesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "ProfilesV2");
        private readonly Dictionary<string, string> _idToCustomName = new Dictionary<string, string>();
        private readonly Dictionary<string, HashSet<string>> _idToProfileNames = new Dictionary<string, HashSet<string>>();
        private readonly ProfileAnalysisService _profileAnalysis;
        private DateTime _lastLogScan = DateTime.MinValue;

        public MonitoringService(ConfigurationService configService, FileLoggingService logger)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _profileAnalysis = new ProfileAnalysisService(logger);
            _logger.LogInfo("[MonitoringService] Service initialized");
        }

        public void RefreshDevices()
        {
            ScanLogs();
            PollRegistry();
            ResolveCustomNames();
        }

        public List<DeviceState> GetKnownDevices()
        {
            lock (_devices)
            {
                return _devices.Values.ToList();
            }
        }

        public async Task StartMonitoringAsync(CancellationToken cancellationToken)
        {
            lock (_lockObject)
            {
                if (_isMonitoring) return;
                _isMonitoring = true;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            _logger.LogInfo("[MonitoringService] Monitoring started");
            _monitoringTask = Task.Run(() => MonitorLoop(_cts.Token));
            await Task.CompletedTask;
        }

        public async Task StopMonitoringAsync()
        {
            lock (_lockObject)
            {
                if (!_isMonitoring) return;
                _isMonitoring = false;
                _cts?.Cancel();
            }

            if (_monitoringTask != null)
            {
                try { await _monitoringTask; } catch (OperationCanceledException) { }
            }

            _logger.LogInfo("[MonitoringService] Monitoring stopped");
        }

        private void MonitorLoop(CancellationToken token)
        {
            IntPtr hKey = IntPtr.Zero;
            try
            {
                int result = RegOpenKeyEx(HKEY_CURRENT_USER, @"Software\Elgato Systems GmbH\StreamDeck", 0, KEY_READ, out hKey);
                if (result != 0)
                {
                    _logger.LogError($"Error opening registry key: {result}. Registry monitoring aborted.");
                    return;
                }

                using (var regChangedEvent = new AutoResetEvent(false))
                {
                    // Initial Scan
                    PollRegistry();
                    ScanLogs();
                    ResolveCustomNames();
                    PublishUpdate();
                    _lastLogScan = DateTime.Now;

                    bool isWatching = false;

                    while (!token.IsCancellationRequested)
                    {
                        if (!isWatching)
                        {
                            RegNotifyChangeKeyValue(hKey, true, REG_NOTIFY_CHANGE_LAST_SET, regChangedEvent.SafeWaitHandle.DangerousGetHandle(), true);
                            isWatching = true;
                        }

                        bool changed = regChangedEvent.WaitOne(1000);

                        if (changed)
                        {
                            isWatching = false;
                            PollRegistry();
                            ResolveCustomNames();
                            PublishUpdate();
                        }

                        if ((DateTime.Now - _lastLogScan).TotalSeconds > 10)
                        {
                            ScanLogs();
                            ResolveCustomNames();
                            PublishUpdate();
                            _lastLogScan = DateTime.Now;
                        }
                    }
                }
            }
            finally
            {
                if (hKey != IntPtr.Zero) RegCloseKey(hKey);
            }
        }

        private void PublishUpdate()
        {
            var data = new StreamDeckData
            {
                Devices = _devices.Values.OrderBy(d => d.Serial).ToList(),
                Timestamp = DateTime.Now,
                HasError = false
            };
            OnDataUpdated(data);
        }

        private void ScanLogs()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string pluginsDir = Path.Combine(appData, "Elgato", "StreamDeck", "Plugins");

                if (!Directory.Exists(pluginsDir)) return;

                var logFiles = new DirectoryInfo(pluginsDir).GetFiles("*.log", SearchOption.AllDirectories)
                                    .OrderBy(f => f.LastWriteTime);

                foreach (var fileInfo in logFiles)
                {
                    try
                    {
                        using (var fs = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                        {
                            string content = sr.ReadToEnd();
                            var match = Regex.Match(content, @"devices"":\[(.*?)\]");
                            if (match.Success)
                            {
                                string devicesJson = match.Groups[1].Value;
                                var deviceMatches = Regex.Matches(devicesJson, @"\{""id"":""([a-f0-9]{32})""[^}]*?""name"":""([^""]+)""");
                                foreach (Match dm in deviceMatches)
                                {
                                    string id = dm.Groups[1].Value;
                                    string name = dm.Groups[2].Value;
                                    _idToCustomName[id] = name;
                                }
                            }
                        }
                    }
                    catch { }
                }

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
                                string? line;
                                while ((line = sr.ReadLine()) != null)
                                {
                                    var match = Regex.Match(line, @"Device: \w+ \(([a-f0-9]{32})\) - Profile: (.+)");
                                    if (match.Success)
                                    {
                                        string id = match.Groups[1].Value;
                                        string profile = match.Groups[2].Value.Trim();
                                        if (!_idToProfileNames.ContainsKey(id))
                                        {
                                            _idToProfileNames[id] = new HashSet<string>();
                                        }
                                        _idToProfileNames[id].Add(profile);
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

        private void ResolveCustomNames()
        {
            foreach (var device in _devices.Values)
            {
                if (string.IsNullOrEmpty(device.ProfileName)) continue;

                foreach (var kvp in _idToProfileNames)
                {
                    if (kvp.Value.Contains(device.ProfileName))
                    {
                        if (_idToCustomName.TryGetValue(kvp.Key, out string? customName))
                        {
                            // Only overwrite if current name is default or empty
                            if (device.DeviceName == "Stream Deck" || string.IsNullOrEmpty(device.DeviceName))
                            {
                                device.DeviceName = customName ?? device.DeviceName;
                            }
                        }
                    }
                }
            }

            // Heuristic matching removed as it conflicts with Registry data
            /*
            var knownCustomNames = _idToCustomName.Values.ToHashSet();
            var unmappedDevices = _devices.Values.Where(d => !knownCustomNames.Contains(d.DeviceName)).ToList();
            var usedCustomNames = _devices.Values.Select(d => d.DeviceName).Where(n => knownCustomNames.Contains(n)).ToHashSet();
            var availableCustomNames = _idToCustomName.Values.Where(n => !usedCustomNames.Contains(n)).ToList();

            if (unmappedDevices.Count == 1 && availableCustomNames.Count == 1)
            {
                unmappedDevices[0].DeviceName = availableCustomNames[0];
            }
            */
        }

        private void PollRegistry()
        {
            try
            {
                _logger.LogInfo("Polling Registry...");
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Elgato Systems GmbH\StreamDeck"))
                {
                    if (key == null)
                    {
                        _logger.LogWarning("Registry key not found.");
                        return;
                    }

                    var bytes = key.GetValue("Devices") as byte[];
                    if (bytes == null)
                    {
                        _logger.LogWarning("Devices value is null.");
                        return;
                    }

                    string raw = Encoding.Unicode.GetString(bytes);
                    // Replace nulls with newlines to preserve separation between strings
                    string clean = raw.Replace("\0", "\n");
                    _logger.LogInfo($"Registry content length: {clean.Length}");

                    var pattern = @"ESDProfilesPreferred[\s\S]*?([a-fA-F0-9-]{36})[\s\S]*?(@\(1\)\[\d+/\d+/(.*?)\])";
                    var matches = Regex.Matches(clean, pattern);
                    _logger.LogInfo($"Found {matches.Count} matches.");

                    var foundSerials = new HashSet<string>();
                    int previousMatchEnd = 0;

                    foreach (Match match in matches)
                    {
                        string uuid = match.Groups[1].Value;
                        string fullId = match.Groups[2].Value;
                        string serial = match.Groups[3].Value.Trim();

                        foundSerials.Add(serial);

                        _logger.LogInfo($"Match: Serial={serial}, UUID={uuid}");

                        if (!_devices.ContainsKey(serial))
                        {
                            _devices[serial] = new DeviceState { Serial = serial, FullId = fullId };
                            _logger.LogInfo($"New device detected: {serial}");
                        }

                        var device = _devices[serial];

                        // Look for DeviceName in the text BEFORE this match (but after the previous match)
                        int searchStart = previousMatchEnd;
                        int searchLength = match.Index - searchStart;
                        
                        string detectedName = "Stream Deck";

                        if (searchLength > 0)
                        {
                            string context = clean.Substring(searchStart, searchLength);
                            // We want the LAST DeviceName in this chunk, as it belongs to the current device block
                            // Capture until a control character (like newline from null replacement)
                            var nameMatches = Regex.Matches(context, @"DeviceName[\W_]*([^\n\r\x00-\x1F]+)");
                            if (nameMatches.Count > 0)
                            {
                                detectedName = nameMatches[nameMatches.Count - 1].Groups[1].Value.Trim();
                            }
                        }

                        if (device.DeviceName != detectedName)
                        {
                            device.DeviceName = detectedName;
                            _logger.LogInfo($"Updated device name for {serial}: {detectedName}");
                        }

                        previousMatchEnd = match.Index + match.Length;

                        if (device.ProfileUuid != uuid)
                        {
                            _logger.LogInfo($"Profile changed for {serial}: {device.ProfileUuid} -> {uuid}");
                            device.ProfileUuid = uuid;
                            device.ProfileName = GetProfileName(uuid);
                            device.Buttons = _profileAnalysis.GetButtonInfo(uuid);
                            device.LastUpdate = DateTime.Now;
                        }
                        else
                        {
                            // Force update for debugging if needed, or just log
                            _logger.LogInfo($"Profile unchanged for {serial}: {uuid}");
                            // Uncomment to force update during debug
                            device.Buttons = _profileAnalysis.GetButtonInfo(uuid);
                        }
                    }

                    // Remove stale devices
                    var staleSerials = _devices.Keys.Where(k => !foundSerials.Contains(k)).ToList();
                    foreach (var stale in staleSerials)
                    {
                        _devices.Remove(stale);
                        _logger.LogInfo($"Removed stale device: {stale}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in PollRegistry: {ex.Message}");
            }
        }

        private string GetProfileName(string uuid)
        {
            if (_profileNameCache.ContainsKey(uuid)) return _profileNameCache[uuid];

            string manifestPath = Path.Combine(_profilesDir, $"{uuid}.sdProfile", "manifest.json");
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
                            _profileNameCache[uuid] = name;
                            return name;
                        }
                    }
                }
                catch { }
            }

            return "Unknown Profile";
        }

        private void OnDataUpdated(StreamDeckData data)
        {
            try
            {
                DataUpdated?.Invoke(this, new DataUpdatedEventArgs(data));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in DataUpdated event: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                StopMonitoringAsync().Wait();
                _cts?.Dispose();
                _logger.LogInfo("[MonitoringService] Service disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during disposal: {ex.Message}");
            }
        }
    }
}