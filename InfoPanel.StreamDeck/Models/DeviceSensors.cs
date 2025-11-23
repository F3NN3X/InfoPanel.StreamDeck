using InfoPanel.Plugins;
using InfoPanel.StreamDeck.Services;
using System.Collections.Generic;

namespace InfoPanel.StreamDeck.Models
{
    public class DeviceSensors
    {
        private readonly IPluginContainer _container;
        private readonly ImageServerService _imageServer;

        public PluginText Serial { get; set; }
        public PluginText ProfileName { get; set; }
        public PluginText ProfileUuid { get; set; }
        public Dictionary<string, PluginText> Buttons { get; set; } = new();
        public Dictionary<string, PluginText> ButtonIcons { get; set; } = new();

        private readonly string _serial;

        public DeviceSensors(IPluginContainer container, string serial, string profileName, string profileUuid, ImageServerService imageServer)
        {
            _container = container;
            _imageServer = imageServer;
            _serial = serial;
            Serial = new PluginText($"serial-{serial}", "Serial Number", serial);
            ProfileName = new PluginText($"profile-{serial}", "Active Profile", profileName);
            ProfileUuid = new PluginText($"uuid-{serial}", "Profile UUID", profileUuid);
        }

        private string GetFormattedName(string key)
        {
            var parts = key.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int c) && int.TryParse(parts[1], out int r))
            {
                return $"R{r + 1}:C{c + 1}";
            }
            return key;
        }

        public void UpdateButton(string key, ButtonInfo info)
        {
            string formattedName = GetFormattedName(key);

            // Update Title
            if (Buttons.TryGetValue(key, out var sensor))
            {
                string newValue = !string.IsNullOrEmpty(info.Title) ? info.Title : "-";
                string newName = sensor.Name;

                if (!string.IsNullOrEmpty(info.Title))
                {
                    newName = info.Title;
                }
                else
                {
                    newName = $"Button {formattedName}";
                }

                if (sensor.Name != newName)
                {
                    _container.Entries.Remove(sensor);
                    var newSensor = new PluginText(sensor.Id, newName, newValue);
                    Buttons[key] = newSensor;
                    _container.Entries.Add(newSensor);
                }
                else
                {
                    sensor.Value = newValue;
                }
            }
            else
            {
                string name = !string.IsNullOrEmpty(info.Title) ? info.Title : $"Button {formattedName}";
                string value = !string.IsNullOrEmpty(info.Title) ? info.Title : "-";
                var newSensor = new PluginText($"btn-{_serial}-{key}", name, value);
                Buttons[key] = newSensor;
                _container.Entries.Add(newSensor);
            }

            // Update Icon
            if (ButtonIcons.TryGetValue(key, out var iconSensor))
            {
                string rawPath = !string.IsNullOrEmpty(info.IconPath) ? info.IconPath : "";
                string newValue = !string.IsNullOrEmpty(rawPath) ? _imageServer.GetUrl(rawPath) : "-";
                string newName = iconSensor.Name;

                if (!string.IsNullOrEmpty(info.Title))
                {
                    newName = $"{info.Title} Icon";
                }
                else
                {
                    newName = $"Button {formattedName} Icon";
                }

                if (iconSensor.Name != newName)
                {
                    _container.Entries.Remove(iconSensor);
                    var newSensor = new PluginText(iconSensor.Id, newName, newValue);
                    ButtonIcons[key] = newSensor;
                    _container.Entries.Add(newSensor);
                }
                else
                {
                    iconSensor.Value = newValue;
                }
            }
            else
            {
                string name = !string.IsNullOrEmpty(info.Title) ? $"{info.Title} Icon" : $"Button {formattedName} Icon";
                string rawPath = !string.IsNullOrEmpty(info.IconPath) ? info.IconPath : "";
                string value = !string.IsNullOrEmpty(rawPath) ? _imageServer.GetUrl(rawPath) : "-";
                var newSensor = new PluginText($"btn-icon-{_serial}-{key}", name, value);
                ButtonIcons[key] = newSensor;
                _container.Entries.Add(newSensor);
            }
        }

        public void RemoveUnusedButtons(IEnumerable<string> activeKeys)
        {
            var activeKeySet = new HashSet<string>(activeKeys);
            var keysToRemove = Buttons.Keys.Where(k => !activeKeySet.Contains(k)).ToList();

            foreach (var key in keysToRemove)
            {
                if (Buttons.TryGetValue(key, out var sensor))
                {
                    _container.Entries.Remove(sensor);
                    Buttons.Remove(key);
                }
                if (ButtonIcons.TryGetValue(key, out var iconSensor))
                {
                    _container.Entries.Remove(iconSensor);
                    ButtonIcons.Remove(key);
                }
            }
        }
    }
}
