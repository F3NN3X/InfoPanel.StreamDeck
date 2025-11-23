using System;
using System.Collections.Generic;

namespace InfoPanel.StreamDeck.Models
{
    public class DeviceState
    {
        public string Serial { get; set; } = "";
        public string FullId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string ProfileUuid { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public Dictionary<string, ButtonInfo> Buttons { get; set; } = new();
        public DateTime LastUpdate { get; set; }
    }

    /// <summary>
    /// Data model for Stream Deck monitoring
    /// </summary>
    public class StreamDeckData
    {
        public List<DeviceState> Devices { get; set; } = new List<DeviceState>();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool HasError { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
