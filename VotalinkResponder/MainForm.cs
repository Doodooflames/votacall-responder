using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VotalinkResponder
{
    public partial class MainForm : Form
    {
        private StyledScrollTextBox _logBox = null!;
        private Button _startButton = null!;
        private Button _stopButton = null!;
        private Button _refreshButton = null!;
        private Label _statusLabel = null!;
        private Label _wsStatusLabel = null!;
        private Label _selectedDeviceLabel = null!;
        private CheckBox _blockTeamsCheckbox = null!;
        private NumericUpDown _wsPortInput = null!;
        private RadioButton _callButtonModeRadio = null!;
        private RadioButton _volumeButtonModeRadio = null!;
        private NumericUpDown _safetyDelayInput = null!;
        private Label _safetyDelayLabel = null!;
        private Panel _safetyDelayPanel = null!;
        private CheckBox _delayAfterHangupCheckbox = null!;
        private TextBox _customCallButtonPatternInput = null!;
        private NumericUpDown _extensionRetryIntervalInput = null!;
        private TextBox _extensionFolderPathInput = null!;
        private Button _extensionFolderBrowseButton = null!;
        private CheckBox _closeToTrayCheckbox = null!;
        private CheckBox _showConsoleCheckbox = null!;
        private Button _checkUpdatesButton = null!;
        private NotifyIcon? _trayIcon;
        private UpdateManager? _updateManager;
        private VotalinkResponderService? _service;
        private List<HidDeviceInfo> _availableDevices = new();
        private AppConfig _config;
        private Panel? _interfaceCardContainer;
        private HidDeviceInfo? _selectedInterface;

        public MainForm(AppConfig config)
        {
            _config = config;
            InitializeComponent();
            LoadConfig();
            RefreshDeviceList();
            InitializeTrayIcon();
            // Initialize update manager after form is loaded (handle is created)
            this.Load += (s, e) => InitializeUpdateManager();
        }

        private void InitializeComponent()
        {
            // Get version from assembly
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionString = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "Unknown";
            
            this.Text = $"Votalink Responder v{versionString}";
            this.Size = new Size(1200, 1330);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(900, 900);

            // Status bar at top
            var statusPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                BackColor = Color.FromArgb(30, 35, 45), // Darker slate header
                Padding = new Padding(20, 15, 20, 15)
            };

            _statusLabel = new Label
            {
                Text = "Status: Stopped",
                Location = new Point(20, 15),
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.White
            };

            _wsStatusLabel = new Label
            {
                Text = "WebSocket: Not started",
                Location = new Point(20, 40),
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 255)
            };

            // Version label in status bar
            var versionLabel = new Label
            {
                Text = $"Version: {versionString}",
                Location = new Point(250, 15),
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(180, 180, 200)
            };

            var wsPortLabel = new Label
            {
                Text = "Port:",
                Location = new Point(20, 60),
                AutoSize = true,
                ForeColor = Color.White
            };

            _wsPortInput = new NumericUpDown
            {
                Location = new Point(60, 58),
                Width = 80,
                Minimum = 1024,
                Maximum = 65535,
                Value = 9231,
                BackColor = Color.White
            };

            _blockTeamsCheckbox = new CheckBox
            {
                Text = "Block Teams Launch",
                Location = new Point(160, 60),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.White
            };

            statusPanel.Controls.AddRange(new Control[] { _statusLabel, _wsStatusLabel, versionLabel, wsPortLabel, _wsPortInput, _blockTeamsCheckbox });

            // Control buttons
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = Color.FromArgb(50, 55, 65), // Medium slate
                Padding = new Padding(20, 15, 20, 15)
            };

            _startButton = new Button
            {
                Text = "▶ Start",
                Location = new Point(20, 15),
                Size = new Size(120, 35),
                BackColor = Color.FromArgb(0, 150, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            _startButton.FlatAppearance.BorderSize = 0;
            _startButton.Click += StartButton_Click;

            _stopButton = new Button
            {
                Text = "■ Stop",
                Location = new Point(150, 15),
                Size = new Size(120, 35),
                BackColor = Color.FromArgb(200, 50, 50),
                ForeColor = Color.White,
                Enabled = false,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            _stopButton.FlatAppearance.BorderSize = 0;
            _stopButton.Click += StopButton_Click;

            _refreshButton = new Button
            {
                Text = "🔄 Refresh Devices",
                Location = new Point(280, 15),
                Size = new Size(150, 35),
                BackColor = Color.FromArgb(100, 100, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9)
            };
            _refreshButton.FlatAppearance.BorderSize = 0;
            _refreshButton.Click += RefreshButton_Click;

            var connectExtensionButton = new Button
            {
                Text = "🔌 Connect to Extension",
                Location = new Point(440, 15),
                Size = new Size(160, 35),
                BackColor = Color.FromArgb(70, 130, 180),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            connectExtensionButton.FlatAppearance.BorderSize = 0;
            connectExtensionButton.Click += ConnectExtensionButton_Click;

            var setupButton = new Button
            {
                Text = "⚙ Setup",
                Location = new Point(610, 15),
                Size = new Size(100, 35),
                BackColor = Color.FromArgb(70, 130, 180),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9),
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            };
            setupButton.FlatAppearance.BorderSize = 0;
            setupButton.Click += SetupButton_Click;

            var debugConsoleButton = new Button
            {
                Text = "🐛 Debug Console",
                Location = new Point(720, 15),
                Size = new Size(130, 35),
                BackColor = Color.FromArgb(150, 100, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9),
                Cursor = Cursors.Hand
            };
            debugConsoleButton.FlatAppearance.BorderSize = 0;
            debugConsoleButton.Click += (s, e) => DebugConsoleForm.Instance.Show();

            var remapCallButton = new Button
            {
                Text = "🔄 Remap Call Button",
                Location = new Point(860, 15),
                Size = new Size(150, 35),
                BackColor = Color.FromArgb(100, 150, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9),
                Cursor = Cursors.Hand
            };
            remapCallButton.FlatAppearance.BorderSize = 0;
            remapCallButton.Click += RemapCallButton_Click;

            buttonPanel.Controls.AddRange(new Control[] { _startButton, _stopButton, _refreshButton, connectExtensionButton, setupButton, debugConsoleButton, remapCallButton });

            // Device selection panel (left side) - redesigned with interface picker and settings
            var devicePanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 550,
                BackColor = Color.FromArgb(50, 55, 65), // Medium slate
                Padding = new Padding(20, 20, 20, 20),
                AutoScroll = true
            };

            // Interface Selection Section
            var interfaceSectionLabel = new Label
            {
                Text = "Interface Selection:",
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(0, 5, 0, 5),
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 220, 255)
            };

            var interfaceDescLabel = new Label
            {
                Text = "Select which interface to monitor:",
                Dock = DockStyle.Top,
                Height = 25,
                Padding = new Padding(0, 0, 0, 5),
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(180, 180, 190),
                AutoSize = false
            };

            // Interface card container (scrollable) - increased height for more room
            _interfaceCardContainer = new Panel
            {
                Dock = DockStyle.Top,
                Height = 400,
                AutoScroll = true,
                BackColor = Color.FromArgb(40, 44, 52),
                Margin = new Padding(0, 0, 0, 20),
                Padding = new Padding(5, 5, 5, 5)
            };
            
            // Handle resize to update card widths
            _interfaceCardContainer.Resize += (s, e) =>
            {
                foreach (Control control in _interfaceCardContainer.Controls)
                {
                    if (control is RoundedCardPanel card)
                    {
                        // Account for scrollbar width (typically 17px) to prevent horizontal scrollbar
                        card.Width = _interfaceCardContainer.Width - 22;
                        foreach (Control label in card.Controls)
                        {
                            if (label is Label lbl)
                            {
                                // Ensure labels fit within card, accounting for padding
                                lbl.Width = card.Width - 24;
                                // Enable text ellipsis for long text
                                lbl.AutoEllipsis = true;
                            }
                        }
                    }
                }
            };

            // Button Mode Selection
            var modeSectionLabel = new Label
            {
                Text = "Button Mode:",
                Dock = DockStyle.Top,
                Height = 35,
                Padding = new Padding(0, 8, 0, 5),
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 220, 255)
            };

            var modePanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = Color.FromArgb(45, 50, 60),
                Padding = new Padding(10, 10, 10, 10),
                Margin = new Padding(0, 0, 0, 20)
            };

            _callButtonModeRadio = new RadioButton
            {
                Text = "Call/Answer Button Mode",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 10),
                AutoSize = true,
                Checked = _config.CallButtonMode,
                BackColor = Color.Transparent,
                Tag = "call"
            };
            _callButtonModeRadio.CheckedChanged += (s, e) =>
            {
                if (_callButtonModeRadio.Checked)
                {
                    _config.CallButtonMode = true;
                    _config.Save();
                    // Ensure safety delay panel is visible
                    if (_safetyDelayPanel != null)
                    {
                        _safetyDelayPanel.Visible = true;
                    }
                }
            };

            _volumeButtonModeRadio = new RadioButton
            {
                Text = "Volume Control Mode",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 35),
                AutoSize = true,
                Checked = !_config.CallButtonMode,
                BackColor = Color.Transparent,
                Tag = "volume"
            };
            _volumeButtonModeRadio.CheckedChanged += (s, e) =>
            {
                if (_volumeButtonModeRadio.Checked)
                {
                    _config.CallButtonMode = false;
                    _config.Save();
                    // Ensure safety delay panel is visible
                    if (_safetyDelayPanel != null)
                    {
                        _safetyDelayPanel.Visible = true;
                    }
                }
            };

            modePanel.Controls.Add(_callButtonModeRadio);
            modePanel.Controls.Add(_volumeButtonModeRadio);
            
            // Safety Delay Panel (shown in both modes now)
            _safetyDelayPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = Color.FromArgb(45, 50, 60),
                Padding = new Padding(10, 10, 10, 10),
                Margin = new Padding(0, 0, 0, 20),
                Visible = true // Always visible now for both modes
            };
            
            _safetyDelayLabel = new Label
            {
                Text = "Safety Delay (seconds):",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 12),
                AutoSize = true
            };
            
            _safetyDelayInput = new NumericUpDown
            {
                Location = new Point(200, 10),
                Width = 80,
                Minimum = 0,
                Maximum = 60,
                Value = _config.SafetyDelaySeconds,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(60, 65, 75),
                ForeColor = Color.FromArgb(220, 220, 230)
            };
            _safetyDelayInput.ValueChanged += (s, e) =>
            {
                _config.SafetyDelaySeconds = (int)_safetyDelayInput.Value;
                _config.Save();
            };
            
            var safetyDelayDescLabel = new Label
            {
                Text = "Prevents accidental call hang-ups (applies to both modes)",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(180, 180, 190),
                Location = new Point(10, 32),
                AutoSize = true
            };
            
            _delayAfterHangupCheckbox = new CheckBox
            {
                Text = "Apply delay after hanging up call",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 52),
                AutoSize = true,
                Checked = _config.DelayAfterHangup,
                BackColor = Color.Transparent
            };
            _delayAfterHangupCheckbox.CheckedChanged += (s, e) =>
            {
                _config.DelayAfterHangup = _delayAfterHangupCheckbox.Checked;
                _config.Save();
            };
            
            _safetyDelayPanel.Controls.Add(_safetyDelayLabel);
            _safetyDelayPanel.Controls.Add(_safetyDelayInput);
            _safetyDelayPanel.Controls.Add(safetyDelayDescLabel);
            _safetyDelayPanel.Controls.Add(_delayAfterHangupCheckbox);

            // Settings Section
            var settingsSectionLabel = new Label
            {
                Text = "Settings:",
                Dock = DockStyle.Top,
                Height = 35,
                Padding = new Padding(0, 8, 0, 5),
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 220, 255)
            };

            var settingsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 390, // Height adjusted for show console checkbox
                BackColor = Color.FromArgb(45, 50, 60),
                Padding = new Padding(10, 10, 10, 10),
                Margin = new Padding(0, 0, 0, 20)
            };

            _blockTeamsCheckbox.Location = new Point(10, 10);
            _blockTeamsCheckbox.AutoSize = true;
            _blockTeamsCheckbox.BackColor = Color.Transparent;

            var autoStartCheckbox = new CheckBox
            {
                Text = "Start automatically when Windows starts",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 35),
                AutoSize = true,
                BackColor = Color.Transparent
            };

            var wsPortLabelSettings = new Label
            {
                Text = "WebSocket Port:",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 65),
                AutoSize = true
            };

            _wsPortInput.Location = new Point(120, 63);
            _wsPortInput.Width = 100;
            _wsPortInput.Font = new Font("Segoe UI", 9);
            _wsPortInput.BackColor = Color.FromArgb(60, 65, 75);
            _wsPortInput.ForeColor = Color.FromArgb(220, 220, 230);

            // Custom Call Button Pattern field
            var customPatternLabel = new Label
            {
                Text = "Call Button Pattern:",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 90),
                AutoSize = true
            };

            _customCallButtonPatternInput = new TextBox
            {
                Location = new Point(10, 110),
                Width = 200,
                Height = 25,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(60, 65, 75),
                ForeColor = Color.FromArgb(220, 220, 230),
                BorderStyle = BorderStyle.FixedSingle
            };
            _customCallButtonPatternInput.TextChanged += (s, e) =>
            {
                _config.CustomCallButtonPattern = string.IsNullOrWhiteSpace(_customCallButtonPatternInput.Text) 
                    ? null 
                    : _customCallButtonPatternInput.Text.Trim();
                _config.Save();
            };

            var customPatternHelpLabel = new Label
            {
                Text = "Exact hex pattern to trigger call answer (e.g., 02-05-00).\nLeave empty to use auto-detection.",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(180, 180, 190),
                Location = new Point(220, 112),
                AutoSize = true,
                MaximumSize = new Size(settingsPanel.Width - 230, 0)
            };

            // Extension Retry Interval field
            var extensionRetryLabel = new Label
            {
                Text = "Extension Retry Interval (seconds):",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 140),
                AutoSize = true
            };

            _extensionRetryIntervalInput = new NumericUpDown
            {
                Location = new Point(10, 160),
                Width = 100,
                Height = 25,
                Minimum = 10,
                Maximum = 600,
                Value = _config.ExtensionRetryIntervalSeconds,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(60, 65, 75),
                ForeColor = Color.FromArgb(220, 220, 230),
                BorderStyle = BorderStyle.FixedSingle
            };
            _extensionRetryIntervalInput.ValueChanged += (s, e) =>
            {
                _config.ExtensionRetryIntervalSeconds = (int)_extensionRetryIntervalInput.Value;
                _config.Save();
            };

            var extensionRetryHelpLabel = new Label
            {
                Text = "How often to retry extension connection if not connected (10-600 seconds)",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(180, 180, 190),
                Location = new Point(120, 163),
                AutoSize = true,
                MaximumSize = new Size(300, 0)
            };

            // Extension Folder Path field
            var extensionFolderLabel = new Label
            {
                Text = "Extension Folder Path:",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 195),
                AutoSize = true
            };

            _extensionFolderPathInput = new TextBox
            {
                Location = new Point(10, 215),
                Width = 380,
                Height = 25,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(60, 65, 75),
                ForeColor = Color.FromArgb(220, 220, 230),
                BorderStyle = BorderStyle.FixedSingle,
                Text = _config.ExtensionFolderPath ?? ""
            };
            _extensionFolderPathInput.TextChanged += (s, e) =>
            {
                _config.ExtensionFolderPath = string.IsNullOrWhiteSpace(_extensionFolderPathInput.Text) 
                    ? null 
                    : _extensionFolderPathInput.Text.Trim();
                _config.Save();
            };

            _extensionFolderBrowseButton = new Button
            {
                Text = "Browse...",
                Location = new Point(400, 214),
                Width = 90,
                Height = 27,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(60, 65, 75),
                ForeColor = Color.FromArgb(220, 220, 230),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _extensionFolderBrowseButton.FlatAppearance.BorderSize = 1;
            _extensionFolderBrowseButton.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 110);
            _extensionFolderBrowseButton.Click += (s, e) =>
            {
                using (var folderDialog = new FolderBrowserDialog())
                {
                    folderDialog.Description = "Select browser extension folder";
                    folderDialog.ShowNewFolderButton = false;
                    
                    // Set initial directory if one is already selected
                    if (!string.IsNullOrWhiteSpace(_extensionFolderPathInput.Text) && Directory.Exists(_extensionFolderPathInput.Text))
                    {
                        folderDialog.SelectedPath = _extensionFolderPathInput.Text;
                    }
                    
                    if (folderDialog.ShowDialog() == DialogResult.OK)
                    {
                        _extensionFolderPathInput.Text = folderDialog.SelectedPath;
                        _config.ExtensionFolderPath = folderDialog.SelectedPath;
                        _config.Save();
                    }
                }
            };

            var extensionFolderHelpLabel = new Label
            {
                Text = "Path to browser extension folder (for auto-update)",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(180, 180, 190),
                Location = new Point(10, 242),
                AutoSize = true,
                MaximumSize = new Size(480, 0)
            };

            // Close to Tray checkbox
            _closeToTrayCheckbox = new CheckBox
            {
                Text = "Close to system tray",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 265),
                AutoSize = true,
                BackColor = Color.Transparent,
                Checked = _config.CloseToTray
            };
            _closeToTrayCheckbox.CheckedChanged += (s, e) =>
            {
                _config.CloseToTray = _closeToTrayCheckbox.Checked;
                _config.Save();
            };

            // Show Console checkbox
            _showConsoleCheckbox = new CheckBox
            {
                Text = "Show console window",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 288),
                AutoSize = true,
                BackColor = Color.Transparent,
                Checked = _config.ShowConsole
            };
            _showConsoleCheckbox.CheckedChanged += (s, e) =>
            {
                _config.ShowConsole = _showConsoleCheckbox.Checked;
                _config.Save();
                
                // Toggle console visibility immediately
                if (_showConsoleCheckbox.Checked)
                {
                    Program.ShowConsoleWindow();
                }
                else
                {
                    Program.HideConsoleWindow();
                }
            };

            settingsPanel.Controls.Add(_blockTeamsCheckbox);
            settingsPanel.Controls.Add(autoStartCheckbox);
            settingsPanel.Controls.Add(wsPortLabelSettings);
            settingsPanel.Controls.Add(_wsPortInput);
            settingsPanel.Controls.Add(customPatternLabel);
            settingsPanel.Controls.Add(_customCallButtonPatternInput);
            settingsPanel.Controls.Add(customPatternHelpLabel);
            settingsPanel.Controls.Add(extensionRetryLabel);
            settingsPanel.Controls.Add(_extensionRetryIntervalInput);
            settingsPanel.Controls.Add(extensionRetryHelpLabel);
            settingsPanel.Controls.Add(extensionFolderLabel);
            settingsPanel.Controls.Add(_extensionFolderPathInput);
            settingsPanel.Controls.Add(_extensionFolderBrowseButton);
            settingsPanel.Controls.Add(extensionFolderHelpLabel);
            settingsPanel.Controls.Add(_closeToTrayCheckbox);
            settingsPanel.Controls.Add(_showConsoleCheckbox);

            // GitHub Update Settings Section
            var githubSectionLabel = new Label
            {
                Text = "Updates:",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(10, 290),
                AutoSize = true
            };

            var githubHelpLabel = new Label
            {
                Text = "Automatic updates checked every 30 minutes",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(180, 180, 190),
                Location = new Point(10, 310),
                AutoSize = true,
                MaximumSize = new Size(480, 0)
            };

            _checkUpdatesButton = new Button
            {
                Text = "Check for Updates",
                Location = new Point(10, 330),
                Width = 150,
                Height = 30,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(70, 130, 180),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _checkUpdatesButton.FlatAppearance.BorderSize = 0;
            _checkUpdatesButton.Click += async (s, e) =>
            {
                _checkUpdatesButton.Enabled = false;
                _checkUpdatesButton.Text = "Checking...";
                if (_updateManager != null)
                {
                    await _updateManager.CheckForUpdatesAsync(silent: false);
                }
                _checkUpdatesButton.Enabled = true;
                _checkUpdatesButton.Text = "Check for Updates";
            };
            
            // Add tooltip or help text about update installation requirements
            var updateHelpTooltip = new ToolTip();
            updateHelpTooltip.SetToolTip(_checkUpdatesButton, 
                "Check for updates. If update installation fails, try running the app as Administrator.\n" +
                $"Log file: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VotalinkResponder", "netsparkle.log")}");

            settingsPanel.Controls.Add(githubSectionLabel);
            settingsPanel.Controls.Add(githubHelpLabel);
            settingsPanel.Controls.Add(_checkUpdatesButton);

            // Selected device display (at bottom)
            _selectedDeviceLabel = new Label
            {
                Name = "selectedDeviceLabel",
                Text = _config.SelectedDeviceName ?? "(Not configured - run Setup)",
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(0, 10, 0, 0),
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(180, 180, 190),
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft
            };

            // Add controls in reverse order (bottom to top)
            devicePanel.Controls.Add(_selectedDeviceLabel);
            devicePanel.Controls.Add(settingsPanel);
            devicePanel.Controls.Add(settingsSectionLabel);
            devicePanel.Controls.Add(_safetyDelayPanel);
            devicePanel.Controls.Add(modePanel);
            devicePanel.Controls.Add(modeSectionLabel);
            devicePanel.Controls.Add(_interfaceCardContainer);
            devicePanel.Controls.Add(interfaceDescLabel);
            devicePanel.Controls.Add(interfaceSectionLabel);
            
            // Load interface cards after form is shown
            this.Load += (s, e) => LoadInterfaceCards();

            // Log panel (right side) - Event Log only
            var logPanel = new Panel
            {
                Dock = DockStyle.Fill
            };

            // Event log label
            var logLabel = new Label
            {
                Text = "Event Log:",
                Dock = DockStyle.Top,
                Height = 25,
                Padding = new Padding(5, 5, 0, 0),
                BackColor = Color.FromArgb(50, 55, 65),
                ForeColor = Color.FromArgb(200, 200, 210)
            };

            _logBox = new StyledScrollTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(25, 28, 35),
                ForeColor = Color.LimeGreen,
                Font = new Font("Consolas", 9)
            };

            logPanel.Controls.Add(_logBox);
            logPanel.Controls.Add(logLabel);

            // Add all panels to form
            this.Controls.Add(logPanel);
            this.Controls.Add(devicePanel);
            this.Controls.Add(buttonPanel);
            this.Controls.Add(statusPanel);
        }

        private void RefreshButton_Click(object? sender, EventArgs e)
        {
            RefreshDeviceList();
        }

        private void ConnectExtensionButton_Click(object? sender, EventArgs e)
        {
            var extensionForm = new ExtensionConnectionForm(_service);
            extensionForm.ShowDialog(this);
        }

        private void SetupButton_Click(object? sender, EventArgs e)
        {
            // Stop service if running
            if (_service != null)
            {
                StopButton_Click(null, EventArgs.Empty);
            }

            // Launch setup wizard
            using (var setupWizard = new SetupWizardForm())
            {
                if (setupWizard.ShowDialog() == DialogResult.OK)
                {
                    // Update config with new selection
                    _config.SelectedDevicePath = setupWizard.SelectedDevicePath;
                    _config.SelectedDeviceName = setupWizard.SelectedDeviceName;
                    _config.SelectedInterfacePath = setupWizard.SelectedDevicePath; // Default to selected interface
                    _config.InterfacePurposes = setupWizard.SelectedInterfacePurposes;
                    _config.SetupCompleted = true;
                    _config.Save();

                    // Refresh UI to show new device and interfaces
                    _selectedDeviceLabel.Text = _config.SelectedDeviceName ?? "(Not configured)";
                    LoadInterfaceCards();

                    LogMessage($"[SETUP] Configuration updated. Selected device: {_config.SelectedDeviceName}");
                }
            }
        }

        private void RemapCallButton_Click(object? sender, EventArgs e)
        {
            using (var remapDialog = new RemapCallButtonDialog())
            {
                if (remapDialog.ShowDialog(this) == DialogResult.OK)
                {
                    // Update the custom call button pattern
                    if (!string.IsNullOrWhiteSpace(remapDialog.DetectedPattern))
                    {
                        _customCallButtonPatternInput.Text = remapDialog.DetectedPattern;
                        _config.CustomCallButtonPattern = remapDialog.DetectedPattern;
                        _config.Save();
                        LogMessage($"✓ Call button pattern updated to: {remapDialog.DetectedPattern}");

                        // Try to find and select the matching interface
                        if (!string.IsNullOrWhiteSpace(remapDialog.DetectedDevicePath))
                        {
                            RefreshDeviceList();
                            
                            // Find the interface that matches the detected device
                            // PRIORITIZE exact device path match first (this ensures we get col4, not col1)
                            HidDeviceInfo? matchingInterface = null;
                            
                            // First, try exact device path match
                            matchingInterface = _availableDevices.FirstOrDefault(d => 
                                d.DevicePath == remapDialog.DetectedDevicePath);
                            
                            // If no exact match, fall back to VID/PID match
                            if (matchingInterface == null)
                            {
                                matchingInterface = _availableDevices.FirstOrDefault(d => 
                                    d.VendorId == remapDialog.DetectedVendorId && 
                                    d.ProductId == remapDialog.DetectedProductId);
                            }

                            if (matchingInterface != null)
                            {
                                // Select this interface
                                SelectInterfaceCard(matchingInterface.DevicePath);
                                LogMessage($"✓ Interface automatically selected: {matchingInterface.DisplayName} (Path: {matchingInterface.DevicePath})");
                                
                                MessageBox.Show(
                                    $"Call button remapped successfully!\n\n" +
                                    $"Pattern: {remapDialog.DetectedPattern}\n" +
                                    $"Interface: {matchingInterface.DisplayName}\n" +
                                    $"Device Path: {matchingInterface.DevicePath}\n\n" +
                                    $"Click 'Start' to apply the changes.",
                                    "Remap Successful",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
                            }
                            else
                            {
                                MessageBox.Show(
                                    $"Call button pattern updated!\n\n" +
                                    $"Pattern: {remapDialog.DetectedPattern}\n" +
                                    $"Detected Device Path: {remapDialog.DetectedDevicePath}\n\n" +
                                    $"Please manually select the correct interface from the list.",
                                    "Pattern Updated",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
                            }
                        }
                        else
                        {
                            MessageBox.Show(
                                $"Call button pattern updated!\n\n" +
                                $"Pattern: {remapDialog.DetectedPattern}",
                                "Pattern Updated",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }
                }
            }
        }

        private void LoadInterfaceCards()
        {
            if (_interfaceCardContainer == null) return;
            
            _interfaceCardContainer.Controls.Clear();
            
            // Only show interfaces if device is configured
            if (string.IsNullOrEmpty(_config.SelectedDevicePath))
            {
                var noConfigLabel = new Label
                {
                    Text = "Run Setup to configure your device",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(150, 150, 150),
                    Font = new Font("Segoe UI", 9, FontStyle.Italic)
                };
                _interfaceCardContainer.Controls.Add(noConfigLabel);
                return;
            }
            
            try
            {
                _availableDevices = HidDeviceMonitor.EnumerateAllDevices();
                
                // Find the configured device and get all its interfaces
                var configuredDevice = _availableDevices.FirstOrDefault(d => d.DevicePath == _config.SelectedDevicePath);
                if (configuredDevice == null)
                {
                    var notFoundLabel = new Label
                    {
                        Text = "Configured device not found.\nPlease run Setup again.",
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleCenter,
                        ForeColor = Color.FromArgb(200, 100, 100),
                        Font = new Font("Segoe UI", 9)
                    };
                    _interfaceCardContainer.Controls.Add(notFoundLabel);
                    return;
                }
                
                // Get all interfaces from the same device (same VID:PID)
                var deviceInterfaces = _availableDevices
                    .Where(d => d.VendorId == configuredDevice.VendorId && d.ProductId == configuredDevice.ProductId)
                    .OrderBy(d => d.UsagePage == 0x0B ? 0 : d.UsagePage == 0x0C ? 1 : 2)
                    .ToList();
                
                int yPos = 5;
                foreach (var device in deviceInterfaces)
                {
                    var card = CreateInterfaceCard(device, yPos);
                    _interfaceCardContainer.Controls.Add(card);
                    yPos += 75; // Card height + spacing
                }
                
                // Select the configured interface if set
                if (!string.IsNullOrEmpty(_config.SelectedInterfacePath))
                {
                    SelectInterfaceCard(_config.SelectedInterfacePath);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[ERROR] Failed to load interface cards: {ex.Message}");
            }
        }
        
        private Panel CreateInterfaceCard(HidDeviceInfo device, int yPos)
        {
            // Get detected purpose from config
            string purpose = "Unknown Function";
            Color accentColor = Color.FromArgb(80, 80, 80);
            
            if (_config.InterfacePurposes.TryGetValue(device.DevicePath, out var interfacePurpose))
            {
                purpose = interfacePurpose.Purpose;
                if (interfacePurpose.DetectedCallButton)
                    accentColor = Color.FromArgb(100, 255, 100); // Green
                else if (interfacePurpose.DetectedVolumeButtons)
                    accentColor = Color.FromArgb(255, 200, 100); // Orange
            }
            else
            {
                // Fallback to inferred purpose
                if (device.UsagePage == 0x0B)
                {
                    purpose = "Telephony (Likely Call Button)";
                    accentColor = Color.FromArgb(100, 200, 255); // Light blue
                }
                else if (device.UsagePage == 0x0C)
                {
                    purpose = "Consumer (Likely Volume Control)";
                    accentColor = Color.FromArgb(255, 200, 100); // Orange
                }
            }
            
            // Account for scrollbar width to prevent horizontal scrollbar
            int cardWidth = _interfaceCardContainer!.Width - 22;
            var card = new RoundedCardPanel
            {
                Location = new Point(5, yPos),
                Size = new Size(cardWidth, 70),
                BackColor = device.DevicePath == _config.SelectedInterfacePath 
                    ? Color.FromArgb(70, 75, 85) 
                    : Color.FromArgb(60, 65, 75),
                BorderStyle = BorderStyle.None,
                Padding = new Padding(12, 10, 12, 10),
                CornerRadius = 8,
                HasShadow = true,
                AccentColor = accentColor,
                Tag = device
            };
            
            // Device name - with ellipsis for long names
            var nameLabel = new Label
            {
                Text = device.DisplayName,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(12, 10),
                Size = new Size(cardWidth - 24, 20),
                AutoSize = false,
                AutoEllipsis = true
            };
            
            // Purpose label - with ellipsis for long text
            var purposeLabel = new Label
            {
                Text = purpose,
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(200, 200, 210),
                Location = new Point(12, 32),
                Size = new Size(cardWidth - 24, 18),
                AutoSize = false,
                AutoEllipsis = true
            };
            
            card.Controls.Add(nameLabel);
            card.Controls.Add(purposeLabel);
            
            // Click handler
            card.Click += (s, e) => SelectInterfaceCard(device.DevicePath);
            nameLabel.Click += (s, e) => SelectInterfaceCard(device.DevicePath);
            purposeLabel.Click += (s, e) => SelectInterfaceCard(device.DevicePath);
            
            // Hover effect
            card.MouseEnter += (s, e) =>
            {
                if (card.Tag != null && ((HidDeviceInfo)card.Tag).DevicePath != _config.SelectedInterfacePath)
                {
                    card.BackColor = Color.FromArgb(70, 75, 85);
                }
            };
            card.MouseLeave += (s, e) =>
            {
                if (card.Tag != null && ((HidDeviceInfo)card.Tag).DevicePath != _config.SelectedInterfacePath)
                {
                    card.BackColor = Color.FromArgb(60, 65, 75);
                }
            };
            
            return card;
        }
        
        private void SelectInterfaceCard(string devicePath)
        {
            _selectedInterface = _availableDevices.FirstOrDefault(d => d.DevicePath == devicePath);
            if (_selectedInterface == null) return;
            
            _config.SelectedInterfacePath = devicePath;
            _config.Save();
            
            // Update card visuals
            foreach (Control control in _interfaceCardContainer!.Controls)
            {
                if (control is RoundedCardPanel card && card.Tag is HidDeviceInfo device)
                {
                    bool isSelected = device.DevicePath == devicePath;
                    card.BackColor = isSelected 
                        ? Color.FromArgb(70, 75, 85) 
                        : Color.FromArgb(60, 65, 75);
                }
            }
            
            _selectedDeviceLabel.Text = $"Selected: {_selectedInterface.DisplayName}";
            LogMessage($"[INTERFACE] Selected interface: {_selectedInterface.DisplayName}");
        }
        
        private void RefreshDeviceList()
        {
            LoadInterfaceCards();
            LogMessage($"[DEVICE] Refreshed interface list");
        }

        private void StartButton_Click(object? sender, EventArgs e)
        {
            try
            {
                // Get selected interface from card selection or config
                HidDeviceInfo? selectedDevice = _selectedInterface;
                
                if (selectedDevice == null && !string.IsNullOrEmpty(_config.SelectedInterfacePath))
                {
                    selectedDevice = _availableDevices.FirstOrDefault(d => d.DevicePath == _config.SelectedInterfacePath);
                }
                
                if (selectedDevice == null && !string.IsNullOrEmpty(_config.SelectedDevicePath))
                {
                    // Fallback to configured device path
                    selectedDevice = _availableDevices.FirstOrDefault(d => d.DevicePath == _config.SelectedDevicePath);
                }

                if (selectedDevice == null)
                {
                    MessageBox.Show("Please select an interface card or run Setup.", "No Interface Selected", 
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                int port = (int)_wsPortInput.Value;
                bool blockTeams = _blockTeamsCheckbox.Checked;

                // Use selected interface
                int safetyDelay = _safetyDelayInput != null 
                    ? (int)_safetyDelayInput.Value 
                    : 0;
                bool delayAfterHangup = _delayAfterHangupCheckbox != null
                    ? _delayAfterHangupCheckbox.Checked
                    : false;
                string? customPattern = string.IsNullOrWhiteSpace(_config.CustomCallButtonPattern) 
                    ? null 
                    : _config.CustomCallButtonPattern.Trim();
                _service = new VotalinkResponderService(port, blockTeams, selectedDevice.DevicePath, skipLogFile: false, safetyDelaySeconds: safetyDelay, delayAfterHangup: delayAfterHangup, customCallButtonPattern: customPattern, callButtonMode: _config.CallButtonMode);
                _service.DeviceDetected += Service_DeviceDetected;
                _service.DeviceRemoved += Service_DeviceRemoved;
                _service.LogMessage += Service_LogMessage;
                _service.WebSocketStatusChanged += Service_WebSocketStatusChanged;
                _service.ExtensionPathDetected += Service_ExtensionPathDetected;
                _service.Start();

                _startButton.Enabled = false;
                _stopButton.Enabled = true;
                _refreshButton.Enabled = false;
                _wsPortInput.Enabled = false;
                _blockTeamsCheckbox.Enabled = false;
                _statusLabel.Text = "Status: Running";
                _statusLabel.ForeColor = Color.Green;

                string deviceName = _config.SelectedDeviceName ?? "Unknown Device";
                LogMessage("=== Votalink Responder Started ===");
                LogMessage($"Monitoring device: {deviceName}");
                LogMessage($"WebSocket server started on port {port}");
                LogMessage($"Teams blocking: {(blockTeams ? "Enabled" : "Disabled")}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start service: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopButton_Click(object? sender, EventArgs e)
        {
            try
            {
                _service?.Stop();
                _service = null;

                _startButton.Enabled = true;
                _stopButton.Enabled = false;
                _refreshButton.Enabled = true;
                _wsPortInput.Enabled = true;
                _blockTeamsCheckbox.Enabled = true;
                _statusLabel.Text = "Status: Stopped";
                _statusLabel.ForeColor = Color.Red;
                _wsStatusLabel.Text = "WebSocket: Not started";

                LogMessage("=== Votalink Responder Stopped ===");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop service: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Service_DeviceDetected(object? sender, DeviceInfoEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Service_DeviceDetected(sender, e)));
                return;
            }

            LogMessage($"[DEVICE] Connected: {e.DeviceName} (VID=0x{e.VendorId:X4} PID=0x{e.ProductId:X4})");
        }

        private void Service_DeviceRemoved(object? sender, DeviceInfoEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Service_DeviceRemoved(sender, e)));
                return;
            }

            LogMessage($"[DEVICE] Disconnected: {e.DeviceName} (VID=0x{e.VendorId:X4} PID=0x{e.ProductId:X4})");
        }

        private void Service_LogMessage(object? sender, string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Service_LogMessage(sender, message)));
                return;
            }

            LogMessage(message);
        }

        private void Service_WebSocketStatusChanged(object? sender, int clientCount)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Service_WebSocketStatusChanged(sender, clientCount)));
                return;
            }

            // clientCount now represents only extensions with active Votacall tabs
            _wsStatusLabel.Text = $"WebSocket: {clientCount} extension(s) with Votacall tab connected";
        }

        private void Service_ExtensionPathDetected(object? sender, string extensionPath)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Service_ExtensionPathDetected(sender, extensionPath)));
                return;
            }

            // Auto-save the detected extension path if not already set
            if (string.IsNullOrWhiteSpace(_config.ExtensionFolderPath))
            {
                _config.ExtensionFolderPath = extensionPath;
                _config.Save();
                
                // Update UI
                if (_extensionFolderPathInput != null)
                {
                    _extensionFolderPathInput.Text = extensionPath;
                }
                
                LogMessage($"[EXTENSION] ✓ Extension folder path auto-saved: {extensionPath}");
            }
            else if (_config.ExtensionFolderPath != extensionPath)
            {
                // Path is already set but different - just log it
                LogMessage($"[EXTENSION] ℹ Extension folder detected: {extensionPath} (using configured path: {_config.ExtensionFolderPath})");
            }
        }

        public void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logBox.AppendText($"[{timestamp}] {message}\r\n");
            _logBox.SelectionStart = _logBox.Text.Length;
            _logBox.ScrollToCaret();

            // Limit log size to prevent memory issues
            if (_logBox.Lines.Length > 1000)
            {
                var lines = _logBox.Lines.Skip(500).ToArray();
                _logBox.Lines = lines;
            }
        }


        private void LoadConfig()
        {
            _wsPortInput.Value = _config.WebSocketPort;
            _blockTeamsCheckbox.Checked = _config.BlockTeams;
            // Ensure safety delay panel is always visible
            if (_safetyDelayPanel != null)
            {
                _safetyDelayPanel.Visible = true;
            }
            if (_customCallButtonPatternInput != null)
            {
                _customCallButtonPatternInput.Text = _config.CustomCallButtonPattern ?? "";
            }
            if (_extensionRetryIntervalInput != null)
            {
                _extensionRetryIntervalInput.Value = _config.ExtensionRetryIntervalSeconds;
            }
            if (_extensionFolderPathInput != null)
            {
                _extensionFolderPathInput.Text = _config.ExtensionFolderPath ?? "";
            }
            if (_closeToTrayCheckbox != null)
            {
                _closeToTrayCheckbox.Checked = _config.CloseToTray;
            }
            if (_showConsoleCheckbox != null)
            {
                _showConsoleCheckbox.Checked = _config.ShowConsole;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // If CloseToTray is enabled and user clicked X, minimize to tray instead
            if (_config.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = true;
                    _trayIcon.ShowBalloonTip(2000, "Votalink Responder", "Application minimized to system tray. Right-click the tray icon to exit.", ToolTipIcon.Info);
                }
                return;
            }

            // Save config
            _config.WebSocketPort = (int)_wsPortInput.Value;
            _config.BlockTeams = _blockTeamsCheckbox.Checked;
            _config.Save();

            // Clean up tray icon
            if (_trayIcon != null)
            {
                try
                {
                    _trayIcon.Visible = false;
                }
                catch
                {
                    // Ignore errors when hiding tray icon (may already be disposed)
                }
                try
                {
                    _trayIcon.Dispose();
                }
                catch
                {
                    // Ignore errors when disposing tray icon
                }
                _trayIcon = null;
            }

            _service?.Stop();
            base.OnFormClosing(e);
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Votalink Responder",
                Visible = _config.CloseToTray && this.WindowState == FormWindowState.Minimized
            };

            // Create context menu
            var contextMenu = new ContextMenuStrip();
            var showMenuItem = new ToolStripMenuItem("Show");
            showMenuItem.Click += (s, e) =>
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            };
            contextMenu.Items.Add(showMenuItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            var exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += (s, e) =>
            {
                // Clean up tray icon before exit
                if (_trayIcon != null)
                {
                    try
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Dispose();
                    }
                    catch
                    {
                        // Ignore errors during cleanup
                    }
                    _trayIcon = null;
                }
                Application.Exit();
            };
            contextMenu.Items.Add(exitMenuItem);

            _trayIcon.ContextMenuStrip = contextMenu;

            // Double-click to show
            _trayIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            
            // Hide to tray when minimized if CloseToTray is enabled
            if (_config.CloseToTray && this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = true;
                }
            }
        }

        private void InitializeUpdateManager()
        {
            // Always initialize update manager (repo URL is hardcoded)
            _updateManager = new UpdateManager(_config, this);
            _updateManager.Initialize();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _updateManager?.Dispose();
            base.OnFormClosed(e);
        }
    }

    public class DeviceInfoEventArgs : EventArgs
    {
        public string DeviceName { get; }
        public ushort VendorId { get; }
        public ushort ProductId { get; }

        public DeviceInfoEventArgs(string deviceName, ushort vendorId, ushort productId)
        {
            DeviceName = deviceName;
            VendorId = vendorId;
            ProductId = productId;
        }
    }
}
