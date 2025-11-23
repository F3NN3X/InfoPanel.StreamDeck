// InfoPanel.StreamDeck v1.1.0
using InfoPanel.Plugins;
using InfoPanel.StreamDeck.Services;
using InfoPanel.StreamDeck.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.StreamDeck
{
    public class StreamDeckMain : BasePlugin
    {
        private string? _configFilePath;
        public override string? ConfigFilePath => _configFilePath;

        private readonly Dictionary<string, DeviceSensors> _activeSensors = new();

        private MonitoringService? _monitoringService;
        private SensorManagementService? _sensorService;
        private ConfigurationService? _configService;
        private FileLoggingService? _loggingService;
        private ImageServerService? _imageServer;
        private CancellationTokenSource? _cancellationTokenSource;

        public StreamDeckMain() : base("InfoPanel.StreamDeck", "Stream Deck Monitor", "Monitors Elgato Stream Deck active profile")
        {
        }

        public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(1000);

        public override void Load(List<IPluginContainer> containers)
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string basePath = assembly.ManifestModule.FullyQualifiedName;
                _configFilePath = $"{basePath}.ini";

                _configService = new ConfigurationService(_configFilePath);
                _loggingService = new FileLoggingService(_configService);
                _loggingService.LogInfo($"[StreamDeck] Config file path: {_configFilePath}");

                _sensorService = new SensorManagementService(_configService, _loggingService);
                _monitoringService = new MonitoringService(_configService, _loggingService);
                _imageServer = new ImageServerService(_loggingService);
                _imageServer.Start();

                _monitoringService.DataUpdated += OnDataUpdated;

                // Initial scan to populate containers
                _monitoringService.RefreshDevices();
                var devices = _monitoringService.GetKnownDevices();

                foreach (var device in devices)
                {
                    var container = new PluginContainer(device.Serial, device.DeviceName);
                    var sensors = new DeviceSensors(container, device.Serial, device.DeviceName, device.ProfileName, device.ProfileUuid, _imageServer);

                    container.Entries.Add(sensors.Serial);
                    container.Entries.Add(sensors.DeviceName);
                    container.Entries.Add(sensors.ProfileName);
                    container.Entries.Add(sensors.ProfileUuid);

                    // Add existing buttons
                    foreach (var btn in device.Buttons)
                    {
                        sensors.UpdateButton(btn.Key, btn.Value);
                    }

                    _activeSensors[device.Serial] = sensors;
                    containers.Add(container);
                }
                _cancellationTokenSource = new CancellationTokenSource();
                _ = StartMonitoringAsync(_cancellationTokenSource.Token);

                _loggingService.LogInfo("[StreamDeck] Plugin initialized successfully");
            }
            catch (Exception ex)
            {
                _loggingService?.LogError($"[StreamDeck] Error during plugin initialization: {ex.Message}", ex);
                throw;
            }
        }

        public override void Initialize()
        {
            // Moved to Load
        }

        public override void Update()
        {
            // Event driven, no polling needed
        }

        public override Task UpdateAsync(CancellationToken token)
        {
            // Event driven, no polling needed
            return Task.CompletedTask;
        }

        private async Task StartMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_monitoringService != null)
                {
                    await _monitoringService.StartMonitoringAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _loggingService?.LogInfo("[StreamDeck] Monitoring cancelled");
            }
            catch (Exception ex)
            {
                _loggingService?.LogError($"[StreamDeck] Error in monitoring: {ex.Message}", ex);
            }
        }

        private void OnDataUpdated(object? sender, DataUpdatedEventArgs e)
        {
            try
            {
                if (_sensorService != null)
                {
                    _sensorService.UpdateSensors(_activeSensors, e.Data);
                }
            }
            catch (Exception ex)
            {
                _loggingService?.LogError($"[StreamDeck] Error updating sensors: {ex.Message}", ex);
            }
        }

        public override void Close()
        {
            try
            {
                _cancellationTokenSource?.Cancel();

                if (_monitoringService != null)
                {
                    _monitoringService.DataUpdated -= OnDataUpdated;
                }

                _monitoringService?.Dispose();
                _imageServer?.Dispose();
                _cancellationTokenSource?.Dispose();

                _loggingService?.LogInfo("[StreamDeck] Plugin disposed successfully");
                _loggingService?.Dispose();
            }
            catch (Exception ex)
            {
                _loggingService?.LogError($"[StreamDeck] Error during disposal: {ex.Message}", ex);
            }
        }
    }
}
