using InfoPanel.StreamDeck.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfoPanel.StreamDeck.Services
{
    public class ProfileAnalysisService
    {
        private readonly string _profilesDir;
        private readonly string _pluginsDir;
        private static readonly Dictionary<string, PluginManifest?> _pluginManifestCache = new();

        public ProfileAnalysisService()
        {
            _profilesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "ProfilesV2");
            _pluginsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "Plugins");
        }

        public Dictionary<string, ButtonInfo> GetButtonInfo(string profileUuid)
        {
            var buttons = new Dictionary<string, ButtonInfo>();
            var profilePath = Path.Combine(_profilesDir, $"{profileUuid}.sdProfile");
            // Use a hardcoded path in the root of the C drive or a known accessible location for debugging
            var debugLogPath = @"C:\Users\Public\infopanel_streamdeck_debug.txt";

            void Log(string message)
            {
                try { File.AppendAllText(debugLogPath, $"{DateTime.Now}: {message}\n"); } catch { }
            }

            Log($"Analyzing profile: {profileUuid}");

            if (!Directory.Exists(profilePath))
            {
                Log($"Profile directory not found: {profilePath}");
                return buttons;
            }
            var profilesSubDir = Path.Combine(profilePath, "Profiles");
            if (!Directory.Exists(profilesSubDir))
            {
                Log("Profiles subdirectory not found.");
                return buttons;
            }

            // Process ALL pages and merge results
            // We prioritize buttons with content (Title/Image)
            var dirsToProcess = Directory.GetDirectories(profilesSubDir);
            Log($"Found {dirsToProcess.Length} page directories.");

            foreach (var dir in dirsToProcess)
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;

                Log($"Processing page: {Path.GetFileName(dir)}");

                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var page = JsonSerializer.Deserialize<PageManifest>(json);

                    if (page?.Controllers != null)
                    {
                        foreach (var controller in page.Controllers)
                        {
                            if (controller.Actions == null) continue;

                            foreach (var action in controller.Actions)
                            {
                                string key = action.Key; // "0,0"
                                var actionData = action.Value;
                                var info = new ButtonInfo();
                                string? image = null;

                                if (actionData.States != null && actionData.States.Length > actionData.State && actionData.State >= 0)
                                {
                                    var state = actionData.States[actionData.State];
                                    info.Title = state.Title ?? "";
                                    image = state.Image;
                                }

                                // Fallback to Action Name
                                if (string.IsNullOrWhiteSpace(info.Title) && !string.IsNullOrWhiteSpace(actionData.Name))
                                {
                                    info.Title = actionData.Name;
                                }

                                // Fallback to UUID
                                if (string.IsNullOrWhiteSpace(info.Title) && !string.IsNullOrWhiteSpace(actionData.UUID))
                                {
                                    var parts = actionData.UUID.Split('.');
                                    if (parts.Length > 0) info.Title = parts.Last();
                                }

                                if (!string.IsNullOrWhiteSpace(image))
                                {
                                    image = image.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

                                    // Strategy 1: Relative to Page Dir
                                    string p1 = Path.Combine(dir, image);
                                    // Strategy 2: Relative to Profile Root
                                    string p2 = Path.Combine(profilePath, image);
                                    // Strategy 3: Profile Root + Images
                                    string p3 = Path.Combine(profilePath, "Images", Path.GetFileName(image));
                                    // Strategy 5: Page Dir + Images (New)
                                    string p5 = Path.Combine(dir, "Images", Path.GetFileName(image));
                                    // Strategy 4: Absolute

                                    if (File.Exists(p1)) info.IconPath = p1;
                                    else if (File.Exists(p2)) info.IconPath = p2;
                                    else if (File.Exists(p3)) info.IconPath = p3;
                                    else if (File.Exists(p5)) info.IconPath = p5;
                                    else if (Path.IsPathRooted(image) && File.Exists(image)) info.IconPath = image;
                                    else
                                    {
                                        Log($"Image not found for {key}: {image}");
                                        Log($"Checked: {p1}");
                                        Log($"Checked: {p2}");
                                        Log($"Checked: {p3}");
                                        Log($"Checked: {p5}");
                                    }
                                }
                                else if (!string.IsNullOrEmpty(actionData.UUID))
                                {
                                    // Try to resolve default icon from plugin
                                    string? pluginUuid = actionData.Plugin?.UUID;
                                    if (string.IsNullOrEmpty(pluginUuid))
                                    {
                                        // Try to guess from Action UUID (e.g. co.meldstudio.streamdeck.show-scene -> co.meldstudio.streamdeck)
                                        var parts = actionData.UUID.Split('.');
                                        if (parts.Length >= 3)
                                        {
                                            pluginUuid = string.Join(".", parts.Take(parts.Length - 1));
                                        }
                                    }

                                    if (!string.IsNullOrEmpty(pluginUuid))
                                    {
                                        string? defaultIcon = ResolveDefaultIcon(pluginUuid, actionData.UUID, actionData.State, Log);
                                        if (!string.IsNullOrEmpty(defaultIcon))
                                        {
                                            info.IconPath = defaultIcon;
                                            Log($"Resolved default icon for {key}: {defaultIcon}");
                                        }
                                    }
                                }

                                // Only add/overwrite if we have something useful
                                // If we already have a button at this key, only overwrite if the new one has more info
                                if (!buttons.ContainsKey(key))
                                {
                                    if (!string.IsNullOrEmpty(info.Title) || !string.IsNullOrEmpty(info.IconPath))
                                    {
                                        buttons[key] = info;
                                        Log($"Added button {key}: Title='{info.Title}', Icon='{info.IconPath}'");
                                    }
                                }
                                else
                                {
                                    var existing = buttons[key];
                                    bool existingHasContent = !string.IsNullOrEmpty(existing.Title) || !string.IsNullOrEmpty(existing.IconPath);
                                    bool newHasContent = !string.IsNullOrEmpty(info.Title) || !string.IsNullOrEmpty(info.IconPath);

                                    // If existing is empty and new is not, overwrite
                                    // If both have content, we might be overwriting a valid button from another page. 
                                    // Ideally we should know the active page. 
                                    // But since we don't, we'll assume the last one processed is active? No, that's risky.
                                    // Let's assume the one with an Image is better?

                                    if (!existingHasContent && newHasContent)
                                    {
                                        buttons[key] = info;
                                        Log($"Updated button {key} (was empty)");
                                    }
                                    else if (string.IsNullOrEmpty(existing.IconPath) && !string.IsNullOrEmpty(info.IconPath))
                                    {
                                        buttons[key] = info; // Prefer one with icon
                                        Log($"Updated button {key} (added icon)");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error processing page {dir}: {ex.Message}");
                }
            }

            return buttons;
        }

        private string? ResolveDefaultIcon(string pluginUuid, string actionUuid, int state, Action<string> log)
        {
            try
            {
                if (!_pluginManifestCache.ContainsKey(pluginUuid))
                {
                    string pluginPath = Path.Combine(_pluginsDir, $"{pluginUuid}.sdPlugin");
                    string manifestPath = Path.Combine(pluginPath, "manifest.json");

                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(manifestPath);
                            var manifest = JsonSerializer.Deserialize<PluginManifest>(json);
                            _pluginManifestCache[pluginUuid] = manifest;
                        }
                        catch (Exception ex)
                        {
                            log($"Error parsing plugin manifest for {pluginUuid}: {ex.Message}");
                            _pluginManifestCache[pluginUuid] = null;
                        }
                    }
                    else
                    {
                        log($"Plugin manifest not found: {manifestPath}");
                        _pluginManifestCache[pluginUuid] = null;
                    }
                }

                var pluginManifest = _pluginManifestCache[pluginUuid];
                if (pluginManifest?.Actions != null)
                {
                    var action = pluginManifest.Actions.FirstOrDefault(a => a.UUID == actionUuid);
                    if (action?.States != null && action.States.Length > state && state >= 0)
                    {
                        var image = action.States[state].Image;
                        if (!string.IsNullOrEmpty(image))
                        {
                            // Plugin icons are relative to the plugin directory
                            string pluginPath = Path.Combine(_pluginsDir, $"{pluginUuid}.sdPlugin");

                            // Handle extensions if missing (Stream Deck supports png, svg, etc)
                            string fullPath = Path.Combine(pluginPath, image);

                            if (File.Exists(fullPath)) return fullPath;
                            if (File.Exists(fullPath + ".png")) return fullPath + ".png";
                            if (File.Exists(fullPath + ".svg")) return fullPath + ".svg";
                            if (File.Exists(fullPath + ".jpg")) return fullPath + ".jpg";

                            // Also check for @2x variants
                            if (File.Exists(fullPath + "@2x.png")) return fullPath + "@2x.png";

                            log($"Default icon not found on disk: {fullPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log($"Error resolving default icon: {ex.Message}");
            }
            return null;
        }

        // JSON Models
        private class RootManifest
        {
            [JsonPropertyName("Pages")]
            public PagesInfo? Pages { get; set; }
        }

        private class PagesInfo
        {
            [JsonPropertyName("Current")]
            public string? Current { get; set; }
        }

        private class PageManifest
        {
            [JsonPropertyName("Controllers")]
            public List<Controller>? Controllers { get; set; }
        }

        private class Controller
        {
            [JsonPropertyName("Actions")]
            public Dictionary<string, ActionData>? Actions { get; set; }
        }

        private class ActionData
        {
            [JsonPropertyName("Name")]
            public string? Name { get; set; }

            [JsonPropertyName("UUID")]
            public string? UUID { get; set; }

            [JsonPropertyName("State")]
            public int State { get; set; }

            [JsonPropertyName("States")]
            public ActionState[]? States { get; set; }

            [JsonPropertyName("Plugin")]
            public PluginRef? Plugin { get; set; }
        }

        private class PluginRef
        {
            [JsonPropertyName("UUID")]
            public string? UUID { get; set; }
        }

        private class ActionState
        {
            [JsonPropertyName("Title")]
            public string? Title { get; set; }

            [JsonPropertyName("Image")]
            public string? Image { get; set; }
        }

        private class PluginManifest
        {
            [JsonPropertyName("Actions")]
            public List<PluginAction>? Actions { get; set; }
        }

        private class PluginAction
        {
            [JsonPropertyName("UUID")]
            public string? UUID { get; set; }

            [JsonPropertyName("States")]
            public ActionState[]? States { get; set; }
        }
    }
}
