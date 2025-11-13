using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VotalinkResponder
{
    public class VotalinkResponderService : IDisposable
    {
        private readonly WebSocketHub _ws;
        private readonly HidDeviceMonitor _hidMonitor;
        private readonly StreamWriter? _logFile;
        private readonly System.Threading.Timer? _teamsBlockerTimer;
        private readonly bool _blockTeams;
        private readonly Dictionary<string, DeviceInfo> _devices = new();
        private DateTime _lastCallButtonPress = DateTime.MinValue;
        private DateTime _lastBroadcastTime = DateTime.MinValue; // Track when we last broadcasted
        private DateTime _lastSuccessfulActionTime = DateTime.MinValue; // Track when we last successfully performed an action
        private string _lastAction = "none"; // Track last action: "answer", "hangup", or "none"
        private readonly HashSet<int> _teamsMinimizedPids = new();
        private readonly HashSet<string> _noiseFilter = new();
        private bool _isCollectingNoise = true;
        private DateTime _collectionStartTime = DateTime.Now;
        private const int NOISE_COLLECTION_SECONDS = 30;
        private int _safetyDelaySeconds = 0; // Safety delay to prevent accidental hang-ups
        private bool _delayAfterHangup = false; // If true, delay applies after hangup. If false, delay only applies after answer.
        private string? _customCallButtonPattern = null; // Custom hex pattern for call button detection

        public event EventHandler<DeviceInfoEventArgs>? DeviceDetected;
        public event EventHandler<DeviceInfoEventArgs>? DeviceRemoved;
        public event EventHandler<string>? LogMessage;
        public event EventHandler<int>? WebSocketStatusChanged;

        // Expose WebSocketHub for extension connection form
        public WebSocketHub? GetWebSocketHub() => _ws;

        public VotalinkResponderService(int wsPort, bool blockTeams, string? selectedDevicePath = null, bool skipLogFile = false, int safetyDelaySeconds = 0, bool delayAfterHangup = false, string? customCallButtonPattern = null)
        {
            _blockTeams = blockTeams;
            _safetyDelaySeconds = safetyDelaySeconds;
            _delayAfterHangup = delayAfterHangup;
            _customCallButtonPattern = customCallButtonPattern;
            _ws = new WebSocketHub(wsPort);
            _ws.LogMessage += (s, msg) => LogMessage?.Invoke(this, msg); // Forward WebSocketHub logs
            _ws.ClientCountChanged += (s, count) => 
            {
                // Count only extensions with active Votacall tabs
                var activeExtensions = _ws.GetConnectedClients()
                    .Count(c => c.IsExtension && c.HasVotacallTab);
                
                WebSocketStatusChanged?.Invoke(this, activeExtensions);
                if (activeExtensions > 0)
                {
                    LogMessage?.Invoke(this, $"[EXTENSION] ✓ Browser extension connected ({activeExtensions} with Votacall tab)");
                }
                else if (count > 0)
                {
                    LogMessage?.Invoke(this, $"[EXTENSION] ⚠ {count} extension(s) connected but no Votacall tabs open");
                }
                else
                {
                    LogMessage?.Invoke(this, "[EXTENSION] ⚠ All browser extensions disconnected");
                }
            };
            _ws.ClientConnected += (s, clientInfo) =>
            {
                if (clientInfo.IsExtension)
                {
                    if (clientInfo.HasVotacallTab)
                    {
                        LogMessage?.Invoke(this, $"[EXTENSION] ✓ Extension connected: {clientInfo.BrowserName} {clientInfo.BrowserVersion} (Votacall tab active)");
                    }
                    else
                    {
                        LogMessage?.Invoke(this, $"[EXTENSION] ⚠ Extension connected: {clientInfo.BrowserName} {clientInfo.BrowserVersion} (no Votacall tab)");
                    }
                    
                    // Try to auto-detect extension folder path
                    if (!string.IsNullOrEmpty(clientInfo.ExtensionName) && !string.IsNullOrEmpty(clientInfo.ExtensionVersion))
                    {
                        TryDetectExtensionPath(clientInfo.BrowserName, clientInfo.ExtensionName, clientInfo.ExtensionVersion);
                    }
                }
            };
            _ws.ClientDisconnected += (s, clientId) =>
            {
                LogMessage?.Invoke(this, $"[EXTENSION] ⚠ Extension disconnected (ID: {clientId.Substring(0, Math.Min(8, clientId.Length))}...)");
            };
            _ws.ExtensionReplyReceived += (s, reply) =>
            {
                var firedMsg = $"[VotalinkResponderService] 🔔 ExtensionReplyReceived EVENT FIRED";
                LogMessage?.Invoke(this, firedMsg);
                DebugConsoleForm.Instance.WriteLine(firedMsg);
                
                var clientMsg = $"[VotalinkResponderService] ClientId: {reply.ClientId}, Success: {reply.Success}";
                LogMessage?.Invoke(this, clientMsg);
                DebugConsoleForm.Instance.WriteLine(clientMsg);
                
                var clientIdShort = reply.ClientId.Length > 8 ? reply.ClientId.Substring(0, 8) + "..." : reply.ClientId;
                if (reply.Success)
                {
                    var successMsg = $"[EXTENSION] ✓ Reply from extension (ID: {clientIdShort}): {reply.Message}";
                    LogMessage?.Invoke(this, successMsg);
                    DebugConsoleForm.Instance.WriteLine(successMsg);
                    
                    // Update last action and reset delay timer only on successful action
                    // Parse action from message or use a default
                    if (reply.Message.Contains("ANSWER") || reply.Message.Contains("answer"))
                    {
                        _lastAction = "answer";
                        _lastSuccessfulActionTime = DateTime.Now;
                        _lastBroadcastTime = DateTime.Now; // Reset broadcast time to start delay period
                        LogMessage?.Invoke(this, "[DELAY] ✓ Answer action successful - delay timer reset");
                    }
                    else if (reply.Message.Contains("HANGUP") || reply.Message.Contains("hangup"))
                    {
                        _lastAction = "hangup";
                        _lastSuccessfulActionTime = DateTime.Now;
                        _lastBroadcastTime = DateTime.Now; // Reset broadcast time to start delay period
                        LogMessage?.Invoke(this, "[DELAY] ✓ Hangup action successful - delay timer reset");
                    }
                }
                else
                {
                    // Reduce log noise for common "no call state" messages (expected when there's no active call)
                    // Only log if it's an actual error, not the normal "no call state" response
                    if (!reply.Message.Contains("Neither ANSWER nor HANGUP button found"))
                    {
                        var failMsg = $"[EXTENSION] ⚠ Reply from extension (ID: {clientIdShort}): {reply.Message}";
                        LogMessage?.Invoke(this, failMsg);
                        DebugConsoleForm.Instance.WriteLine(failMsg);
                    }
                    // For "no call state" messages, we don't log them to reduce noise
                    // This is expected behavior when there's no active call
                    
                    // Don't reset delay on failed actions - let the existing delay continue
                    if (reply.Message.Contains("no call state"))
                    {
                        // Only log delay message if it's not the common "no call state" message
                        // LogMessage?.Invoke(this, "[DELAY] No action taken - delay timer unchanged");
                    }
                }
            };

            if (!skipLogFile)
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"votalink-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                _logFile = new StreamWriter(logPath, append: true) { AutoFlush = true };
            }

            // If a specific device is selected, only monitor that device
            IEnumerable<string>? targetPaths = null;
            if (!string.IsNullOrEmpty(selectedDevicePath))
            {
                targetPaths = new[] { selectedDevicePath };
            }

            _hidMonitor = new HidDeviceMonitor(
                message => LogMessage?.Invoke(this, message),
                OnHidReport,
                targetPaths);

            if (_blockTeams)
            {
                _teamsBlockerTimer = new System.Threading.Timer(BlockTeamsLaunch, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            }

            // End noise collection after period
            var collectionTimer = new System.Threading.Timer(EndNoiseCollection, null, TimeSpan.FromSeconds(NOISE_COLLECTION_SECONDS), Timeout.InfiniteTimeSpan);
        }

        public void Start()
        {
            // Check for conflicting applications
            CheckConflictingApplications();
            
            _ws.Start();
            LogMessage?.Invoke(this, $"[EXTENSION] WebSocket server started on port {_ws.GetPort()} (waiting for browser extensions to connect...)");
            
            if (!string.IsNullOrWhiteSpace(_customCallButtonPattern))
            {
                LogMessage?.Invoke(this, $"[CALL-BUTTON] Custom pattern enabled: '{_customCallButtonPattern}' - Only this exact pattern will trigger call answer");
            }
            else
            {
                LogMessage?.Invoke(this, "[CALL-BUTTON] Using auto-detection mode - all non-volume button patterns will trigger call answer");
            }
            
            _hidMonitor.Start();
            LogMessage?.Invoke(this, $"[NOISE-FILTER] Collecting noise patterns for {NOISE_COLLECTION_SECONDS} seconds...");
        }

        private void CheckConflictingApplications()
        {
            try
            {
                // Check for Yealink USB Connect
                var yealinkProcesses = Process.GetProcessesByName("YealinkUSBConnect")
                    .Concat(Process.GetProcessesByName("Yealink USB Connect"))
                    .Concat(Process.GetProcessesByName("YUC"))
                    .ToList();
                
                if (yealinkProcesses.Count > 0)
                {
                    LogMessage?.Invoke(this, $"[INFO] ℹ Yealink USB Connect is running ({yealinkProcesses.Count} process(es))");
                    LogMessage?.Invoke(this, "[INFO] If button events aren't detected, check USB Connect settings:");
                    LogMessage?.Invoke(this, "[INFO]   → Enable '3rd-party calling control' or 'Allow third-party applications'");
                    LogMessage?.Invoke(this, "[INFO]   → This allows button events to be sent to other applications");
                }
                
                // Check for Teams
                var teamsProcesses = Process.GetProcessesByName("Teams")
                    .Concat(Process.GetProcessesByName("ms-teams"))
                    .Concat(Process.GetProcessesByName("Microsoft Teams"))
                    .ToList();
                
                if (teamsProcesses.Count > 0)
                {
                    LogMessage?.Invoke(this, $"[WARNING] ⚠ Microsoft Teams is running ({teamsProcesses.Count} process(es))");
                    LogMessage?.Invoke(this, "[WARNING] Teams may have exclusive access to the headset.");
                }
            }
            catch
            {
                // Ignore errors checking processes
            }
        }

        public void Stop()
        {
            _teamsBlockerTimer?.Dispose();
            _hidMonitor?.Dispose();
            _ws?.Stop();
            _logFile?.Close();
        }

        private void OnHidReport(HidDirectReport report)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var hex = BitConverter.ToString(report.Data);

            // Track device
            string deviceKey = $"{report.VendorId:X4}:{report.ProductId:X4}";
            if (!_devices.ContainsKey(deviceKey))
            {
                string deviceName = GetDeviceName(report.VendorId, report.ProductId);
                var deviceInfo = new DeviceInfo(deviceName, report.VendorId, report.ProductId);
                _devices[deviceKey] = deviceInfo;
                DeviceDetected?.Invoke(this, new DeviceInfoEventArgs(deviceName, report.VendorId, report.ProductId));
            }

            // Detect call button - support multiple Yealink models and custom patterns
            // Note: WH62 may not have Teams mode in USB Connect, but button still works
            bool isCallButton = false;
            bool isYealinkDevice = report.VendorId == 0x6993 || report.VendorId == 0x2F68 || report.VendorId == 0x19F7;

            // Check custom pattern first (if set) - supports single patterns or press/release pairs (comma-separated)
            if (!string.IsNullOrWhiteSpace(_customCallButtonPattern))
            {
                string normalizedHex = NormalizeHexPattern(hex);
                
                // Check if pattern contains a comma (press,release pair)
                bool patternMatches = false;
                if (_customCallButtonPattern.Contains(','))
                {
                    // Pattern is a pair: "press,release" - ONLY match the PRESS part (first pattern)
                    // This prevents triggering on both press and release events
                    string[] patternParts = _customCallButtonPattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (patternParts.Length > 0)
                    {
                        string pressPattern = patternParts[0];
                        string? releasePattern = patternParts.Length > 1 ? patternParts[1] : null;
                        string normalizedPressPattern = NormalizeHexPattern(pressPattern);
                        
                        // Check if this is the release pattern (should be skipped)
                        if (releasePattern != null)
                        {
                            string normalizedReleasePattern = NormalizeHexPattern(releasePattern);
                            bool isReleasePattern = normalizedHex == normalizedReleasePattern || 
                                                   normalizedHex.StartsWith(normalizedReleasePattern + "-") ||
                                                   normalizedHex.StartsWith(normalizedReleasePattern);
                            
                            if (isReleasePattern)
                            {
                                // This is the release pattern - skip it (only trigger on press)
                                // Log at debug level to avoid confusion
                                return; // Exit early, don't process release
                            }
                        }
                        
                        // Check if this matches the press pattern
                        patternMatches = normalizedHex == normalizedPressPattern || 
                                        normalizedHex.StartsWith(normalizedPressPattern + "-") ||
                                        normalizedHex.StartsWith(normalizedPressPattern);
                    }
                }
                else
                {
                    // Single pattern - exact match or prefix match (handles padding zeros)
                    string normalizedPattern = NormalizeHexPattern(_customCallButtonPattern);
                    patternMatches = normalizedHex == normalizedPattern || 
                                     normalizedHex.StartsWith(normalizedPattern + "-") ||
                                     normalizedHex.StartsWith(normalizedPattern);
                }
                
                if (patternMatches)
                {
                    isCallButton = true;
                    _lastCallButtonPress = DateTime.Now;
                    _teamsMinimizedPids.Clear();
                    LogMessage?.Invoke(this, $"[CALL-BUTTON] *** CALL BUTTON PRESSED (Custom Pattern: {_customCallButtonPattern}) *** Data={hex}");
                    BroadcastCallAction(report, hex, timestamp);
                    BlockTeamsLaunch(null);
                }
            }
            // Fall back to default detection if no custom pattern
            else if (isYealinkDevice)
            {
                // WH64 specific pattern (known pattern)
                if (report.VendorId == 0x6993 && report.ProductId == 0xB0AE)
                {
                    if (hex == "9B-01-00")
                    {
                        isCallButton = true;
                        _lastCallButtonPress = DateTime.Now;
                        _teamsMinimizedPids.Clear();
                        LogMessage?.Invoke(this, $"[CALL-BUTTON] *** CALL BUTTON PRESSED (WH64) ***");
                        BroadcastCallAction(report, hex, timestamp);
                        BlockTeamsLaunch(null);
                    }
                }
                // WH62 and other Yealink models - look for non-volume button presses
                // This works even without Teams mode in USB Connect
                else
                {
                    // Volume buttons are typically 01-01 (up) and 01-02 (down)
                    // Call buttons usually have different patterns
                    // Filter out zero/empty patterns and known volume patterns
                    if (hex != "01-01" && hex != "01-02" && hex != "00-00" && hex.Length > 0)
                    {
                        bool hasActivity = report.Data.Any(b => b != 0x00);
                        if (hasActivity)
                        {
                            // Additional check: filter out very long patterns (likely diagnostic spam)
                            if (report.Data.Length <= 8)
                            {
                                isCallButton = true;
                                _lastCallButtonPress = DateTime.Now;
                                LogMessage?.Invoke(this, $"[CALL-BUTTON] *** POTENTIAL CALL BUTTON (Yealink VID=0x{report.VendorId:X4} PID=0x{report.ProductId:X4}) *** Data={hex}");
                                BroadcastCallAction(report, hex, timestamp);
                                BlockTeamsLaunch(null);
                            }
                        }
                    }
                }
            }

            // Noise filtering
            string normalizedKey = $"{report.VendorId:X4}:{report.ProductId:X4}:";
            if (report.Data.Length >= 8)
                normalizedKey += BitConverter.ToString(report.Data, 0, 8);
            else
                normalizedKey += hex;

            bool isNoise = false;
            if (_isCollectingNoise)
            {
                if (!isCallButton)
                    _noiseFilter.Add(normalizedKey);
            }
            else
            {
                isNoise = _noiseFilter.Contains(normalizedKey);
                if (!isNoise && report.VendorId == 0x6993 && report.ProductId == 0xB0AE && report.Data.Length >= 2)
                {
                    byte firstByte = report.Data[0];
                    byte secondByte = report.Data[1];
                    if (report.Data.Length > 8 || (firstByte == 0xC8 && (secondByte == 0x3B || secondByte == 0x3A || secondByte == 0x39 || secondByte == 0x03 || secondByte == 0x00 || secondByte == 0x38)))
                        isNoise = true;
                }
            }

            if (!isNoise || isCallButton)
            {
                var payload = "{" +
                              "\"type\":\"direct-hid\"," +
                              "\"at\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "," +
                              "\"vendorId\":" + report.VendorId + "," +
                              "\"productId\":" + report.ProductId + "," +
                              "\"dataHex\":\"" + hex + "\"," +
                              "\"path\":\"" + report.DevicePath.Replace("\\", "\\\\").Replace("\"", "'") + "\"}";
                _ws.Broadcast(payload);
            }

            if (isCallButton || (!isNoise && !_isCollectingNoise))
            {
                _logFile?.WriteLine($"[{timestamp}] [HID-DIRECT] REPORT | VID=0x{report.VendorId:X4} PID=0x{report.ProductId:X4} Data={hex}");
            }
        }

        private void BroadcastCallAction(HidDirectReport report, string hex, string timestamp)
        {
            // Extension now checks what's present before acting, so we only need to send one message
            // The extension will intelligently decide whether to answer or hangup based on what buttons are present
            BroadcastCallAnswer(report, hex, timestamp);
        }

        private void BroadcastCallAnswer(HidDirectReport report, string hex, string timestamp)
        {
            // Count only extensions with active Votacall tabs
            var activeExtensions = _ws.GetConnectedClients()
                .Where(c => c.IsExtension && c.HasVotacallTab)
                .ToList();
            int activeCount = activeExtensions.Count;
            int totalCount = _ws.ClientCount;
            
            // Check safety delay based on last action and user preference
            if (_safetyDelaySeconds > 0)
            {
                bool shouldDelay = false;
                double secondsSinceLastAction = (DateTime.Now - _lastSuccessfulActionTime).TotalSeconds;
                
                // Determine if delay should apply:
                // - If last action was "answer" → always delay (prevent accidental hangup)
                // - If last action was "hangup" → delay only if user enabled "Delay after hangup"
                // - If last action was "none" → no delay (first action)
                if (_lastAction == "answer")
                {
                    // Always delay after answer to prevent accidental hangup
                    shouldDelay = secondsSinceLastAction < _safetyDelaySeconds;
                }
                else if (_lastAction == "hangup")
                {
                    // Delay after hangup only if user enabled the option
                    shouldDelay = _delayAfterHangup && secondsSinceLastAction < _safetyDelaySeconds;
                }
                // else: _lastAction == "none" → no delay needed
                
                if (shouldDelay)
                {
                    double remainingDelay = _safetyDelaySeconds - secondsSinceLastAction;
                    string delayReason = _lastAction == "answer" 
                        ? "after answering call" 
                        : "after hanging up call";
                    LogMessage?.Invoke(this, $"[CALL-BUTTON] Safety delay active ({delayReason}) - ignoring button press ({remainingDelay:F1}s remaining)");
                    return;
                }
            }
            
            if (activeCount == 0)
            {
                if (totalCount > 0)
                {
                    LogMessage?.Invoke(this, "[EXTENSION] ⚠ WARNING: Extensions connected but no Votacall tabs open! Button press detected but no active extension to receive it.");
                    LogMessage?.Invoke(this, "[EXTENSION] Make sure the Votacall webapp is open in a browser tab.");
                }
                else
                {
                    LogMessage?.Invoke(this, "[EXTENSION] ⚠ WARNING: No browser extensions connected! Button press detected but no extension to receive it.");
                    LogMessage?.Invoke(this, "[EXTENSION] Make sure the extension is installed and the Votacall webapp is open.");
                }
            }
            else
            {
                LogMessage?.Invoke(this, $"[EXTENSION] Preparing to send button press to {activeCount} active extension(s) with Votacall tabs...");
            }
            
            var payload = "{" +
                          "\"type\":\"call-answer\"," +
                          "\"at\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "," +
                          "\"vendorId\":" + report.VendorId + "," +
                          "\"productId\":" + report.ProductId + "," +
                          "\"dataHex\":\"" + hex + "\"," +
                          "\"button\":\"answer\"}";
            
            LogMessage?.Invoke(this, $"[EXTENSION] Broadcasting message: {{\"type\":\"call-answer\",\"vendorId\":{report.VendorId},\"productId\":{report.ProductId},\"dataHex\":\"{hex}\"}}");
            
            // Only send to extensions with active Votacall tabs
            int sentCount = _ws.BroadcastWithCount(payload, (info) => info.IsExtension && info.HasVotacallTab);
            // Don't update _lastBroadcastTime here - only update on successful action from extension reply
            // This ensures consecutive presses don't reset the delay
            
            if (sentCount > 0)
            {
                LogMessage?.Invoke(this, $"[EXTENSION] ✓ SUCCESS: Message sent to {sentCount} extension(s) with active Votacall tabs");
                LogMessage?.Invoke(this, "[EXTENSION] Extension will check for ANSWER or HANGUP button and act accordingly");
            }
            else
            {
                LogMessage?.Invoke(this, "[EXTENSION] ✗ FAILED: Message could not be sent (no active extensions with Votacall tabs)");
            }
        }

        private void BlockTeamsLaunch(object? state)
        {
            if (!_blockTeams) return;

            try
            {
                bool wasButtonPressedRecently = (DateTime.Now - _lastCallButtonPress).TotalSeconds < 2.0;
                if (!wasButtonPressedRecently)
                {
                    _teamsMinimizedPids.Clear();
                    return;
                }

                var teamsProcesses = Process.GetProcessesByName("Teams")
                    .Concat(Process.GetProcessesByName("ms-teams"))
                    .Concat(Process.GetProcessesByName("Microsoft Teams"))
                    .ToList();

                foreach (var proc in teamsProcesses)
                {
                    try
                    {
                        if (_teamsMinimizedPids.Contains(proc.Id)) continue;

                        IntPtr hwnd = proc.MainWindowHandle;
                        if (hwnd != IntPtr.Zero)
                        {
                            bool isVisible = IsWindowVisible(hwnd);
                            IntPtr foregroundHwnd = GetForegroundWindow();
                            bool isForeground = hwnd == foregroundHwnd;

                            if (isVisible || isForeground)
                            {
                                ShowWindow(hwnd, SW_MINIMIZE);
                                _teamsMinimizedPids.Add(proc.Id);
                                LogMessage?.Invoke(this, $"[TEAMS-BLOCKER] Minimized Teams window (PID {proc.Id})");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void EndNoiseCollection(object? state)
        {
            _isCollectingNoise = false;
            LogMessage?.Invoke(this, $"[NOISE-FILTER] Collection complete! Identified {_noiseFilter.Count} noise patterns.");
        }

        private string GetDeviceName(ushort vid, ushort pid)
        {
            return vid switch
            {
                0x6993 => "Yealink WH64",
                0x2F68 => "Yealink Device",
                0x1395 => "EPOS/Sennheiser",
                0x0B0E => "Jabra",
                0x047F => "Plantronics/Poly",
                0x046D => "Logitech",
                _ => $"Unknown Device (VID=0x{vid:X4})"
            };
        }

        /// <summary>
        /// Normalizes hex pattern for comparison (handles dashes, spaces, case)
        /// Examples: "02-05-00", "02 05 00", "020500" all become "02-05-00"
        /// </summary>
        private string NormalizeHexPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "";

            // Remove spaces and convert to uppercase
            string normalized = pattern.Replace(" ", "").Replace("-", "").ToUpperInvariant();
            
            // Re-add dashes every 2 characters for readability
            if (normalized.Length >= 2)
            {
                var parts = new List<string>();
                for (int i = 0; i < normalized.Length; i += 2)
                {
                    if (i + 2 <= normalized.Length)
                        parts.Add(normalized.Substring(i, 2));
                    else
                        parts.Add(normalized.Substring(i));
                }
                return string.Join("-", parts);
            }
            
            return normalized;
        }
        

        [DllImport("User32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("User32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("User32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;

        public void Dispose()
        {
            Stop();
        }

        private void TryDetectExtensionPath(string browserName, string extensionName, string extensionVersion)
        {
            // Run search in background thread to avoid blocking
            Task.Run(() =>
            {
                try
                {
                    // Get the unique marker from the extension fingerprint
                    // We'll search for background.js files containing this marker
                    string uniqueMarker = "UNIQUE_MARKER_VOTALINK_RESPONDER_EXTENSION_2025_11_13";
                    
                    string? foundPath = null;
                    
                    // First, search common locations (fast)
                    var commonPaths = new List<string>();
                    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    commonPaths.Add(Path.Combine(userProfile, "Downloads"));
                    commonPaths.Add(Path.Combine(userProfile, "Desktop"));
                    commonPaths.Add(Path.Combine(userProfile, "Documents"));
                    commonPaths.Add(Path.Combine(userProfile, "Documents", "votacall-extension"));
                    
                    // Current directory and nearby
                    var currentDir = AppContext.BaseDirectory;
                    commonPaths.Add(currentDir);
                    commonPaths.Add(Path.GetDirectoryName(currentDir) ?? "");
                    commonPaths.Add(Path.GetDirectoryName(Path.GetDirectoryName(currentDir) ?? "") ?? "");
                    commonPaths.Add(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(currentDir) ?? "") ?? "", "votacall-extension"));
                    
                    // Search common paths first (quick)
                    foreach (var searchPath in commonPaths)
                    {
                        if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath))
                            continue;
                        
                        foundPath = SearchForExtensionInDirectory(searchPath, uniqueMarker, extensionName, extensionVersion, maxDepth: 5);
                        if (foundPath != null)
                            break;
                    }
                    
                    // If not found in common locations, search user's entire profile (slower but still reasonable)
                    if (foundPath == null)
                    {
                        foundPath = SearchForExtensionInDirectory(userProfile, uniqueMarker, extensionName, extensionVersion, maxDepth: 8);
                    }
                    
                    if (foundPath != null)
                    {
                        // Notify that we found the path (event will be handled by MainForm)
                        LogMessage?.Invoke(this, $"[EXTENSION] ✓ Auto-detected extension folder: {foundPath}");
                        
                        // Trigger event to save the path
                        ExtensionPathDetected?.Invoke(this, foundPath);
                    }
                    else
                    {
                        LogMessage?.Invoke(this, "[EXTENSION] ⚠ Could not auto-detect extension folder. Please set it manually in Settings.");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, $"[EXTENSION] ⚠ Error detecting extension path: {ex.Message}");
                }
            });
        }
        
        private string? SearchForExtensionInDirectory(string directory, string uniqueMarker, string extensionName, string extensionVersion, int maxDepth)
        {
            try
            {
                var backgroundJsFiles = SearchFiles(directory, "background.js", maxDepth: maxDepth);
                
                foreach (var bgFile in backgroundJsFiles)
                {
                    try
                    {
                        // Check if this background.js contains our unique marker
                        var content = File.ReadAllText(bgFile);
                        if (content.Contains(uniqueMarker))
                        {
                            // Found it! Get the folder containing this background.js
                            var extensionFolder = Path.GetDirectoryName(bgFile);
                            if (!string.IsNullOrEmpty(extensionFolder) && Directory.Exists(extensionFolder))
                            {
                                // Verify it has manifest.json and content.js
                                var hasManifest = File.Exists(Path.Combine(extensionFolder, "manifest.json"));
                                var hasContentJs = File.Exists(Path.Combine(extensionFolder, "content.js"));
                                
                                if (hasManifest && hasContentJs)
                                {
                                    // Double-check manifest matches
                                    try
                                    {
                                        var manifestContent = File.ReadAllText(Path.Combine(extensionFolder, "manifest.json"));
                                        if (manifestContent.Contains(extensionName) && manifestContent.Contains($"\"{extensionVersion}\""))
                                        {
                                            return extensionFolder;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip files we can't read
                        continue;
                    }
                }
            }
            catch
            {
                // Skip directories we can't access
            }
            
            return null;
        }
        
        private List<string> SearchFiles(string directory, string fileName, int maxDepth, int currentDepth = 0)
        {
            var results = new List<string>();
            
            if (currentDepth >= maxDepth)
                return results;
            
            try
            {
                // Skip system directories and common exclusions
                var dirName = Path.GetFileName(directory)?.ToLower() ?? "";
                if (dirName.StartsWith("$") || dirName == "system volume information" || 
                    dirName == "recovery" || dirName == "windows" || dirName == "program files" ||
                    dirName == "program files (x86)" || dirName == "programdata")
                {
                    return results;
                }
                
                // Search for the file in current directory
                var files = Directory.GetFiles(directory, fileName, SearchOption.TopDirectoryOnly);
                results.AddRange(files);
                
                // If we found it, we can stop searching deeper (extension folder found)
                if (results.Count > 0)
                    return results;
                
                // Search subdirectories (limit to avoid too deep recursion)
                var subdirs = Directory.GetDirectories(directory);
                foreach (var subdir in subdirs)
                {
                    try
                    {
                        results.AddRange(SearchFiles(subdir, fileName, maxDepth, currentDepth + 1));
                        // If we found it, stop searching
                        if (results.Count > 0)
                            break;
                    }
                    catch
                    {
                        // Skip directories we can't access
                        continue;
                    }
                }
            }
            catch
            {
                // Skip directories we can't access
            }
            
            return results;
        }

        public event EventHandler<string>? ExtensionPathDetected;

        private class DeviceInfo
        {
            public string Name { get; }
            public ushort VendorId { get; }
            public ushort ProductId { get; }

            public DeviceInfo(string name, ushort vendorId, ushort productId)
            {
                Name = name;
                VendorId = vendorId;
                ProductId = productId;
            }
        }
    }
}

