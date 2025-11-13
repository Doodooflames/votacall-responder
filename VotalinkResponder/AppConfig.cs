using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VotalinkResponder
{
    public class AppConfig
    {
        public string? SelectedDevicePath { get; set; }
        public string? SelectedDeviceName { get; set; }
        public string? SelectedInterfacePath { get; set; } // The specific interface to monitor
        public int WebSocketPort { get; set; } = 9231;
        public bool BlockTeams { get; set; } = true;
        public bool SetupCompleted { get; set; } = false;
        public bool RunOnStartup { get; set; } = false;
        public Dictionary<string, InterfacePurpose> InterfacePurposes { get; set; } = new(); // DevicePath -> Purpose mapping
        public bool CallButtonMode { get; set; } = true; // true = Call/Answer, false = Volume Control
        public int SafetyDelaySeconds { get; set; } = 2; // Delay in seconds before allowing another button press
        public bool DelayAfterHangup { get; set; } = false; // If true, delay applies after hangup. If false, delay only applies after answer.
        public string? CustomCallButtonPattern { get; set; } // Custom hex pattern for call button (e.g., "02-05-00", "9B-01-00")
        public int ExtensionRetryIntervalSeconds { get; set; } = 60; // Retry extension connection every N seconds (default 60)
        public string? ExtensionFolderPath { get; set; } // Path to browser extension folder for auto-update
        public bool CloseToTray { get; set; } = false; // If true, clicking X minimizes to tray instead of closing
        public bool ShowConsole { get; set; } = false; // If true, shows the console window for debugging output

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VotalinkResponder",
            "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    return config ?? new AppConfig();
                }
            }
            catch { }

            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(ConfigPath)!;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }

    public class InterfacePurpose
    {
        public string Purpose { get; set; } = ""; // "Call Button", "Volume Control", "Unknown", etc.
        public bool DetectedCallButton { get; set; }
        public bool DetectedVolumeButtons { get; set; }
    }
}
