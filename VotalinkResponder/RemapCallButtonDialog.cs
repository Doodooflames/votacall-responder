using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VotalinkResponder
{
    public partial class RemapCallButtonDialog : Form
    {
        private Label _statusLabel = null!;
        private Label _detectedPatternLabel = null!;
        private Label _deviceInfoLabel = null!;
        private Button _cancelButton = null!;
        private Button _confirmButton = null!;
        private HidDeviceMonitor? _hidMonitor;
        private string? _detectedPattern;
        private string? _pressPattern;
        private string? _releasePattern;
        private string? _detectedDevicePath;
        private ushort _detectedVendorId;
        private ushort _detectedProductId;
        private bool _patternDetected = false;
        private bool _waitingForRelease = false;
        private const int RELEASE_TIMEOUT_MS = 2000; // Wait up to 2 seconds for release

        public string? DetectedPattern => _detectedPattern;
        public string? DetectedDevicePath => _detectedDevicePath;
        public ushort DetectedVendorId => _detectedVendorId;
        public ushort DetectedProductId => _detectedProductId;

        public RemapCallButtonDialog()
        {
            InitializeComponent();
            StartMonitoring();
        }

        private void InitializeComponent()
        {
            this.Text = "Remap Call Button";
            this.Size = new Size(500, 350);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(50, 55, 65);

            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                BackColor = Color.FromArgb(50, 55, 65)
            };

            var titleLabel = new Label
            {
                Text = "Remap Call Button",
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(20, 20)
            };

            var instructionLabel = new Label
            {
                Text = "Press the call button on your headset now.",
                Font = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(220, 220, 230),
                AutoSize = true,
                Location = new Point(20, 60)
            };

            _statusLabel = new Label
            {
                Text = "⏳ Waiting for button press...",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 200, 100),
                AutoSize = true,
                Location = new Point(20, 100)
            };

            _detectedPatternLabel = new Label
            {
                Text = "Detected Pattern: -",
                Font = new Font("Consolas", 10),
                ForeColor = Color.FromArgb(180, 255, 180),
                AutoSize = true,
                Location = new Point(20, 140)
            };

            _deviceInfoLabel = new Label
            {
                Text = "Device: -",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(200, 200, 220),
                AutoSize = true,
                Location = new Point(20, 170)
            };

            var helpLabel = new Label
            {
                Text = "The app will detect the button press (and release if available)\nand automatically update the call button pattern and interface selection.\nUsing both press and release makes detection more precise.",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(150, 150, 170),
                AutoSize = true,
                Location = new Point(20, 210),
                MaximumSize = new Size(460, 0)
            };

            _confirmButton = new Button
            {
                Text = "Confirm & Apply",
                Size = new Size(140, 35),
                Location = new Point(200, 260),
                BackColor = Color.FromArgb(0, 150, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Enabled = false
            };
            _confirmButton.FlatAppearance.BorderSize = 0;
            _confirmButton.Click += ConfirmButton_Click;

            _cancelButton = new Button
            {
                Text = "Cancel",
                Size = new Size(100, 35),
                Location = new Point(350, 260),
                BackColor = Color.FromArgb(100, 100, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9)
            };
            _cancelButton.FlatAppearance.BorderSize = 0;
            _cancelButton.Click += (s, e) => this.DialogResult = DialogResult.Cancel;

            mainPanel.Controls.AddRange(new Control[] 
            { 
                titleLabel, 
                instructionLabel, 
                _statusLabel, 
                _detectedPatternLabel, 
                _deviceInfoLabel, 
                helpLabel,
                _confirmButton,
                _cancelButton
            });

            this.Controls.Add(mainPanel);
        }

        private void StartMonitoring()
        {
            // Get all available device paths to monitor
            var devices = HidDeviceMonitor.EnumerateAllDevices();
            var devicePaths = devices.Select(d => d.DevicePath).ToList();

            _hidMonitor = new HidDeviceMonitor(
                log => { /* Log to console if needed */ },
                OnHidReport,
                devicePaths
            );

            _hidMonitor.Start();
        }

        private void OnHidReport(HidDirectReport report)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<HidDirectReport>(OnHidReport), report);
                return;
            }

            // If we already completed detection, ignore further reports
            if (_patternDetected && !_waitingForRelease)
            {
                return;
            }

            var hex = System.BitConverter.ToString(report.Data);
            
            // Filter out volume buttons
            if (hex == "01-01" || hex == "01-02" || string.IsNullOrWhiteSpace(hex))
            {
                return;
            }

            // Filter out very long patterns (likely diagnostic spam)
            if (report.Data.Length > 8)
            {
                return;
            }

            // Check if there's actual activity
            bool hasActivity = report.Data.Any(b => b != 0x00);
            if (!hasActivity && !_waitingForRelease)
            {
                return; // Only allow zero patterns if we're waiting for release
            }

            // Filter out known spam patterns for Yealink WH64
            if (report.VendorId == 0x6993 && report.ProductId == 0xB0AE && report.Data.Length >= 2)
            {
                byte firstByte = report.Data[0];
                byte secondByte = report.Data[1];
                if (firstByte == 0xC8 && (secondByte == 0x3B || secondByte == 0x3A || secondByte == 0x39 || 
                    secondByte == 0x03 || secondByte == 0x00 || secondByte == 0x38))
                {
                    return; // This is spam, ignore it
                }
            }

            // Check if this is a press or release
            bool looksLikePress = false;
            bool looksLikeRelease = false;
            
            if (report.Data.Length >= 3)
            {
                // Check if the second byte (or middle bytes) are non-zero - indicates a press
                // For patterns like 9B-01-00, the second byte (01) indicates press
                // For patterns like 9B-00-00, the second byte (00) indicates release
                if (report.Data.Length >= 2 && report.Data[1] != 0x00)
                {
                    looksLikePress = true;
                }
                else if (report.Data.Length >= 2 && report.Data[1] == 0x00 && hasActivity)
                {
                    // Same first byte but second byte is zero - likely a release
                    if (_pressPattern != null && hex.StartsWith(_pressPattern.Split('-')[0]))
                    {
                        looksLikeRelease = true;
                    }
                }
            }
            else if (report.Data.Length == 2)
            {
                // For 2-byte patterns, check if second byte is non-zero
                if (report.Data[1] != 0x00)
                {
                    looksLikePress = true;
                }
                else if (report.Data[1] == 0x00 && hasActivity)
                {
                    // Check if this matches the press pattern's first byte
                    if (_pressPattern != null && hex.StartsWith(_pressPattern.Split('-')[0]))
                    {
                        looksLikeRelease = true;
                    }
                }
            }
            else
            {
                // Single byte or unknown - accept it if it's not all zeros
                if (hasActivity)
                {
                    looksLikePress = true;
                }
            }

            // Handle press detection
            if (looksLikePress && !_waitingForRelease)
            {
                // We have a button press!
                _pressPattern = hex;
                _detectedDevicePath = report.DevicePath;
                _detectedVendorId = report.VendorId;
                _detectedProductId = report.ProductId;
                _waitingForRelease = true;

                // Update UI
                _statusLabel.Text = "✓ Press detected! Waiting for release...";
                _statusLabel.ForeColor = Color.FromArgb(100, 255, 100);
                _detectedPatternLabel.Text = $"Press: {hex}";
                _deviceInfoLabel.Text = $"Device: VID=0x{report.VendorId:X4} PID=0x{report.ProductId:X4}\nPath: {report.DevicePath}";
                
                // Start timeout task to finalize if no release is detected
                Task.Run(async () =>
                {
                    await Task.Delay(RELEASE_TIMEOUT_MS);
                    if (this.InvokeRequired)
                    {
                        this.Invoke(new Action(() =>
                        {
                            if (_waitingForRelease && !_patternDetected)
                            {
                                // No release detected, use just the press pattern
                                FinalizePattern(_pressPattern);
                            }
                        }));
                    }
                });
            }
            // Handle release detection
            else if (looksLikeRelease && _waitingForRelease)
            {
                // Check if this release matches the press pattern (same device, similar pattern)
                if (report.DevicePath == _detectedDevicePath && 
                    report.VendorId == _detectedVendorId && 
                    report.ProductId == _detectedProductId)
                {
                    _releasePattern = hex;
                    
                    // Combine press and release
                    string combinedPattern = $"{_pressPattern},{_releasePattern}";
                    FinalizePattern(combinedPattern);
                }
            }
        }

        private void FinalizePattern(string pattern)
        {
            _detectedPattern = pattern;
            _patternDetected = true;
            _waitingForRelease = false;

            // Update UI
            if (_releasePattern != null)
            {
                _statusLabel.Text = "✓ Press and release detected!";
                _detectedPatternLabel.Text = $"Pattern: {pattern}\n(Press: {_pressPattern}, Release: {_releasePattern})";
            }
            else
            {
                _statusLabel.Text = "✓ Button press detected!";
                _detectedPatternLabel.Text = $"Pattern: {pattern}";
            }
            _confirmButton.Enabled = true;
        }

        private void ConfirmButton_Click(object? sender, EventArgs e)
        {
            if (_patternDetected && !string.IsNullOrWhiteSpace(_detectedPattern))
            {
                this.DialogResult = DialogResult.OK;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _hidMonitor?.Dispose();
            base.OnFormClosed(e);
        }
    }
}

