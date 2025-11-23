using InfoPanel.Plugins;
using InfoPanel.StreamDeck.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.StreamDeck.Services
{
    public class SensorManagementService
    {
        private readonly ConfigurationService _configService;
        private readonly FileLoggingService _logger;
        private readonly object _sensorLock = new();

        public SensorManagementService(ConfigurationService configService, FileLoggingService logger)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void UpdateSensors(Dictionary<string, DeviceSensors> activeSensors, StreamDeckData data)
        {
            if (data == null) return;

            lock (_sensorLock)
            {
                try
                {
                    foreach (var device in data.Devices)
                    {
                        if (activeSensors.TryGetValue(device.Serial, out var sensors))
                        {
                            sensors.Serial.Value = device.Serial;
                            sensors.ProfileName.Value = device.ProfileName;
                            sensors.ProfileUuid.Value = device.ProfileUuid;

                            foreach (var btn in device.Buttons)
                            {
                                sensors.UpdateButton(btn.Key, btn.Value);
                            }
                            sensors.RemoveUnusedButtons(device.Buttons.Keys);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[SensorManagementService] Error updating sensors: {ex.Message}");
                }
            }
        }
    }
}
