using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VotalinkResponder
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();
        
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeConsole();
        
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        
        public static void ShowConsoleWindow()
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                AllocConsole();
                Console.WriteLine("Votalink Responder - Console output enabled for debugging");
            }
            else
            {
                ShowWindow(GetConsoleWindow(), SW_SHOW);
            }
        }
        
        public static void HideConsoleWindow()
        {
            IntPtr consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
            {
                ShowWindow(consoleWindow, SW_HIDE);
            }
        }
        
        [STAThread]
        private static void Main()
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Load config
                var config = AppConfig.Load();
                
                // Only show console if enabled in config
                if (config.ShowConsole)
                {
                    AllocConsole();
                    Console.WriteLine("Votalink Responder - Console output enabled for debugging");
                }

                // Show setup wizard if not completed
                if (!config.SetupCompleted || string.IsNullOrEmpty(config.SelectedDevicePath))
                {
                    using (var setupWizard = new SetupWizardForm())
                    {
                        if (setupWizard.ShowDialog() == DialogResult.OK)
                        {
                            config.SelectedDevicePath = setupWizard.SelectedDevicePath;
                            config.SelectedDeviceName = setupWizard.SelectedDeviceName;
                            config.SetupCompleted = true;
                            config.Save();
                        }
                        else
                        {
                            // User cancelled setup
                            return;
                        }
                    }
                }

                // Show main form
                Application.Run(new MainForm(config));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start application: {ex.Message}\n\n{ex.StackTrace}", 
                    "Votalink Responder Error", 
                    MessageBoxButtons.OK, 
                    MessageBoxIcon.Error);
            }
        }
    }
}

