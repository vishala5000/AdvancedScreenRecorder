using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenRecorderApp {
    public partial class MainWindow : Window {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        
        private const int VK_ESCAPE = 0x1B;
        private const int WM_HOTKEY = 0x0312;
        
        private const int HOTKEY_START = 1;
        private const int HOTKEY_PAUSE = 2;
        private const int HOTKEY_STOP = 3;
        private const int HOTKEY_DRAW = 4;
        private const int HOTKEY_SELECT_AREA = 5;
        
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        private Process ffmpegProcess;
        private bool isRecording = false;
        private bool isPaused = false;
        private int segmentCount = 0;
        private DrawingOverlay overlay;
        private ClickOverlay clickOverlay;
        private DispatcherTimer drawTimer;
        
        private string tempFolder;
        private string outputFolder;
        private string appFolder;
        private string logFile;
        private int screenWidth;
        private int screenHeight;
        
        private Rect? selectedArea = null;
        
        private Forms.NotifyIcon trayIcon;
        private Forms.ContextMenuStrip trayMenu;

        public MainWindow() {
            InitializeComponent();
            
            appFolder = AppDomain.CurrentDomain.BaseDirectory;
            outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VRec");
            tempFolder = Path.Combine(Path.GetTempPath(), "VRec_Temp_" + Process.GetCurrentProcess().Id);
            logFile = Path.Combine(outputFolder, "vrec_log.txt");
            
            screenWidth = GetSystemMetrics(0);
            screenHeight = GetSystemMetrics(1);
            
            try {
                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
                Log("Output folder: " + outputFolder);
            } catch (Exception ex) {
                outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                Log("Failed to create Videos folder, using Desktop: " + ex.Message);
            }
            
            string iconPath = Path.Combine(appFolder, "icon.png");
            if (File.Exists(iconPath)) {
                try { 
                    this.Icon = new BitmapImage(new Uri(iconPath));
                    Log("Icon loaded successfully");
                } catch (Exception ex) {
                    Log("Failed to load icon: " + ex.Message);
                }
            }

            InitializeTrayIcon();
            CheckFFmpeg();
            InitDrawTimer();
            clickOverlay = new ClickOverlay();
            
            this.Loaded += MainWindow_Loaded;
            this.StateChanged += MainWindow_StateChanged;
            
            Log("MainWindow initialized. Screen: " + screenWidth + "x" + screenHeight);
        }

        private void InitializeTrayIcon() {
            try {
                trayMenu = new Forms.ContextMenuStrip();
                trayMenu.BackColor = System.Drawing.Color.FromArgb(26, 26, 26);
                trayMenu.ForeColor = System.Drawing.Color.White;
                trayMenu.ShowImageMargin = false;
                
                var showItem = new Forms.ToolStripMenuItem("📺 Show V Rec");
                showItem.Click += (s, e) => ShowWindow();
                
                var startItem = new Forms.ToolStripMenuItem("▶️ Start Recording");
                startItem.Click += (s, e) => Dispatcher.Invoke(() => StartRec_Click(null, null));
                
                var pauseItem = new Forms.ToolStripMenuItem("⏸️ Pause Recording");
                pauseItem.Click += (s, e) => Dispatcher.Invoke(() => PauseRec_Click(null, null));
                
                var stopItem = new Forms.ToolStripMenuItem("⏹️ Stop Recording");
                stopItem.Click += (s, e) => Dispatcher.Invoke(() => StopRec_Click(null, null));
                
                var drawItem = new Forms.ToolStripMenuItem("✏️ Toggle Draw Mode");
                drawItem.Click += (s, e) => Dispatcher.Invoke(() => Draw_Click(null, null));
                
                var selectAreaItem = new Forms.ToolStripMenuItem("📐 Select Recording Area");
                selectAreaItem.Click += (s, e) => Dispatcher.Invoke(() => SelectArea_Click(null, null));
                
                var exitItem = new Forms.ToolStripMenuItem("❌ Exit");
                exitItem.Click += (s, e) => ExitApplication();
                
                trayMenu.Items.Add(showItem);
                trayMenu.Items.Add(new Forms.ToolStripSeparator());
                trayMenu.Items.Add(startItem);
                trayMenu.Items.Add(pauseItem);
                trayMenu.Items.Add(stopItem);
                trayMenu.Items.Add(drawItem);
                trayMenu.Items.Add(selectAreaItem);
                trayMenu.Items.Add(new Forms.ToolStripSeparator());
                trayMenu.Items.Add(exitItem);
                
                trayIcon = new Forms.NotifyIcon {
                    Visible = true,
                    ContextMenuStrip = trayMenu,
                    Text = "V Rec - Ready"
                };
                
                string iconFilePath = Path.Combine(appFolder, "icon.ico");
                string iconPngPath = Path.Combine(appFolder, "icon.png");
                
                // EXPLICITLY use System.Drawing types to avoid WPF conflicts
                if (File.Exists(iconFilePath)) {
                    trayIcon.Icon = new System.Drawing.Icon(iconFilePath);
                } else if (File.Exists(iconPngPath)) {
                    using (var bmp = new System.Drawing.Bitmap(iconPngPath)) {
                        IntPtr hIcon = bmp.GetHicon();
                        trayIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
                    }
                } else {
                    trayIcon.Icon = System.Drawing.SystemIcons.Application;
                }
                
                trayIcon.DoubleClick += (s, e) => ShowWindow();
                Log("System tray icon initialized");
            } catch (Exception ex) {
                Log("Failed to initialize tray icon: " + ex.Message);
            }
        }

        private void ShowWindow() {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
        }

        private void MinimizeToTray() {
            this.Hide();
            trayIcon.ShowBalloonTip(2000, "V Rec", "App is running in system tray.", Forms.ToolTipIcon.Info);
        }

        private void ExitApplication() {
            try {
                if (ffmpegProcess != null && !ffmpegProcess.HasExited) ffmpegProcess.Kill();
            } catch { }
            try {
                if (trayIcon != null) {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            } catch { }
            Application.Current.Shutdown();
        }

        private void MainWindow_StateChanged(object sender, EventArgs e) {
            if (this.WindowState == WindowState.Minimized) MinimizeToTray();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            var helper = new WindowInteropHelper(this);
            IntPtr hWnd = helper.Handle;
            
            RegisterHotKey(hWnd, HOTKEY_START, MOD_CONTROL | MOD_SHIFT, 0x52);
            RegisterHotKey(hWnd, HOTKEY_PAUSE, MOD_CONTROL | MOD_SHIFT, 0x50);
            RegisterHotKey(hWnd, HOTKEY_STOP, MOD_CONTROL | MOD_SHIFT, 0x53);
            RegisterHotKey(hWnd, HOTKEY_DRAW, MOD_CONTROL | MOD_SHIFT, 0x44);
            RegisterHotKey(hWnd, HOTKEY_SELECT_AREA, MOD_CONTROL | MOD_SHIFT, 0x41);
            
            HwndSource source = HwndSource.FromHwnd(hWnd);
            source.AddHook(HwndHook);
            Log("Global hotkeys registered");
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == WM_HOTKEY) {
                int hotkeyId = wParam.ToInt32();
                switch (hotkeyId) {
                    case HOTKEY_START: Dispatcher.Invoke(() => StartRec_Click(null, null)); handled = true; break;
                    case HOTKEY_PAUSE: Dispatcher.Invoke(() => PauseRec_Click(null, null)); handled = true; break;
                    case HOTKEY_STOP: Dispatcher.Invoke(() => StopRec_Click(null, null)); handled = true; break;
                    case HOTKEY_DRAW: Dispatcher.Invoke(() => Draw_Click(null, null)); handled = true; break;
                    case HOTKEY_SELECT_AREA: Dispatcher.Invoke(() => SelectArea_Click(null, null)); handled = true; break;
                }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e) {
            try {
                var helper = new WindowInteropHelper(this);
                UnregisterHotKey(helper.Handle, HOTKEY_START);
                UnregisterHotKey(helper.Handle, HOTKEY_PAUSE);
                UnregisterHotKey(helper.Handle, HOTKEY_STOP);
                UnregisterHotKey(helper.Handle, HOTKEY_DRAW);
                UnregisterHotKey(helper.Handle, HOTKEY_SELECT_AREA);
            } catch { }
            try {
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            } catch { }
            base.OnClosed(e);
        }

        private void Log(string message) {
            try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + message + Environment.NewLine); } catch { }
        }

        private void UpdateTrayTooltip(string status) {
            try { if (trayIcon != null) trayIcon.Text = "V Rec - " + status; } catch { }
        }

        private void CheckFFmpeg() {
            try {
                string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
                if (!File.Exists(ffmpegPath)) {
                    StatusText.Text = "Error: FFmpeg not found!";
                    UpdateTrayTooltip("FFmpeg Missing!");
                    MessageBox.Show("FFmpeg is missing from: " + appFolder, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                } else {
                    StatusText.Text = "Ready. Screen: " + screenWidth + "x" + screenHeight + ". Videos: " + outputFolder;
                    UpdateTrayTooltip("Ready");
                }
            } catch { StatusText.Text = "Ready."; }
        }

        private void InitDrawTimer() {
            drawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            drawTimer.Tick += DrawTimer_Tick;
        }

        private void DrawTimer_Tick(object sender, EventArgs e) {
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) {
                if (overlay != null && overlay.IsVisible) overlay.StopDrawing();
            }
        }

        private void SelectArea_Click(object sender, RoutedEventArgs e) {
            try {
                var areaSelector = new AreaSelectionOverlay();
                bool? result = areaSelector.ShowDialog();
                if (result == true && areaSelector.SelectedArea.HasValue) {
                    selectedArea = areaSelector.SelectedArea.Value;
                    ResetAreaButton.Visibility = Visibility.Visible;
                    StatusText.Text = $"Area selected: {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}";
                    trayIcon.ShowBalloonTip(2000, "V Rec", $"Recording area set to {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}", Forms.ToolTipIcon.Info);
                }
            } catch (Exception ex) {
                MessageBox.Show("Error selecting area: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetArea_Click(object sender, RoutedEventArgs e) {
            selectedArea = null;
            ResetAreaButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Recording full screen. Videos saved to: " + outputFolder;
        }

        private void StartRec_Click(object sender, RoutedEventArgs e) {
            string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) {
                MessageBox.Show("FFmpeg is missing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (isRecording && !isPaused) return;
            if (!isRecording) {
                segmentCount = 0;
                try {
                    if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
                    Directory.CreateDirectory(tempFolder);
                } catch (Exception ex) {
                    MessageBox.Show("Error creating temp folder: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            try {
                string outFile = Path.Combine(tempFolder, "seg_" + segmentCount + ".mp4");
                string args;
                
                if (selectedArea.HasValue) {
                    args = $"-y -f gdigrab -framerate 30 -video_size {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height} -offset_x {(int)selectedArea.Value.X} -offset_y {(int)selectedArea.Value.Y} -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -pix_fmt yuv420p \"{outFile}\"";
                } else {
                    args = $"-y -f gdigrab -framerate 30 -video_size {screenWidth}x{screenHeight} -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -pix_fmt yuv420p \"{outFile}\"";
                }
                
                ffmpegProcess = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = ffmpegPath, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardError = true, WorkingDirectory = tempFolder
                    }
                };
                
                ffmpegProcess.ErrorDataReceived += (s, ev) => { if (!string.IsNullOrEmpty(ev.Data)) Log("FFmpeg: " + ev.Data); };
                ffmpegProcess.Start();
                ffmpegProcess.BeginErrorReadLine();
                
                isRecording = true;
                isPaused = false;
                segmentCount++;
                clickOverlay.Start();
                StatusText.Text = selectedArea.HasValue ? $"Recording area: {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}..." : "Recording full screen...";
                UpdateTrayTooltip("🔴 Recording...");
                trayIcon.ShowBalloonTip(2000, "V Rec", "Recording started. UI hidden.", Forms.ToolTipIcon.Info);
                this.Hide();
            } catch (Exception ex) {
                MessageBox.Show("Error starting recording: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PauseRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording || isPaused) return;
            isPaused = true;
            StopFFmpegGracefully();
            StatusText.Text = "Paused.";
            UpdateTrayTooltip("⏸️ Paused");
            if (!this.IsVisible) { ShowWindow(); trayIcon.ShowBalloonTip(2000, "V Rec", "Recording paused.", Forms.ToolTipIcon.Info); }
        }

        private void StopFFmpegGracefully() {
            try {
                if (ffmpegProcess != null && !ffmpegProcess.HasExited) {
                    try { ffmpegProcess.StandardInput.WriteLine("q"); ffmpegProcess.StandardInput.Flush(); } catch { }
                    if (!ffmpegProcess.WaitForExit(5000)) { try { ffmpegProcess.Kill(); } catch { } }
                }
            } catch { }
        }

        private async void StopRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording) return;
            string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
            StopFFmpegGracefully();
            clickOverlay.Stop();

            StatusText.Text = "Processing & Mixing Audio... Please wait.";
            UpdateTrayTooltip("⚙️ Processing...");
            StartRecButton.IsEnabled = false;
            
            await Task.Run(() => {
                try {
                    string finalVideo = Path.Combine(tempFolder, "final_video.mp4");
                    var validSegments = Directory.GetFiles(tempFolder, "seg_*.mp4").Where(f => new FileInfo(f).Length > 1000).ToArray();
                    
                    if (validSegments.Length == 0) throw new Exception("No valid video segments found.");
                    
                    if (validSegments.Length > 1) {
                        string listFile = Path.Combine(tempFolder, "list.txt");
                        File.WriteAllLines(listFile, validSegments.OrderBy(f => f).Select(f => "file '" + Path.GetFullPath(f).Replace("'", "''") + "'"));
                        var concatProcess = Process.Start(new ProcessStartInfo { FileName = ffmpegPath, Arguments = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{finalVideo}\"", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tempFolder });
                        concatProcess.WaitForExit();
                        if (concatProcess.ExitCode != 0 || !File.Exists(finalVideo)) throw new Exception("FFmpeg concat failed.");
                    } else {
                        File.Copy(validSegments[0], finalVideo, true);
                    }

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string finalFilename = Path.Combine(outputFolder, "VRec_" + timestamp + ".mp4");

                    string bgPath = Path.Combine(appFolder, "bg.mp3");
                    if (File.Exists(bgPath)) {
                        var mixProcess = Process.Start(new ProcessStartInfo { FileName = ffmpegPath, Arguments = $"-y -i \"{finalVideo}\" -stream_loop -1 -i \"{bgPath}\" -c:v copy -c:a aac -shortest -map 0:v:0 -map 1:a:0 \"{finalFilename}\"", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tempFolder });
                        mixProcess.WaitForExit();
                        if (mixProcess.ExitCode != 0 || !File.Exists(finalFilename)) throw new Exception("FFmpeg audio mix failed.");
                        try { File.Delete(finalVideo); } catch { }
                    } else {
                        File.Move(finalVideo, finalFilename);
                    }

                    if (!File.Exists(finalFilename)) throw new Exception("Final output file was not created.");
                    
                    try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
                    
                    Dispatcher.Invoke(() => {
                        trayIcon.ShowBalloonTip(3000, "V Rec", "Video saved to: " + finalFilename, Forms.ToolTipIcon.Info);
                    });
                } catch (Exception ex) {
                    Dispatcher.Invoke(() => {
                        trayIcon.ShowBalloonTip(3000, "V Rec - Error", "Error: " + ex.Message, Forms.ToolTipIcon.Error);
                    });
                }
            });

            StatusText.Text = selectedArea.HasValue ? $"Ready. Area: {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}." : "Ready. Videos saved to: " + outputFolder;
            UpdateTrayTooltip("Ready");
            StartRecButton.IsEnabled = true;
            isRecording = false;
            isPaused = false;
            if (!this.IsVisible) ShowWindow();
        }

        private void Draw_Click(object sender, RoutedEventArgs e) {
            try {
                if (overlay == null) overlay = new DrawingOverlay();
                overlay.Show(); overlay.Activate(); overlay.StartDrawing(); drawTimer.Start();
                StatusText.Text = "Drawing mode. Hold left mouse. Press Esc to stop.";
            } catch (Exception ex) {
                MessageBox.Show("Error starting draw mode: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e) { HelpPopup.IsOpen = !HelpPopup.IsOpen; }
        private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => MinimizeToTray();
        private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => this.DragMove();
    }
}
