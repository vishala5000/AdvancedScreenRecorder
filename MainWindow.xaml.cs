using System;
using System.Diagnostics;
using System.Drawing;
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
        
        // Area selection
        private Rect? selectedArea = null;
        
        // System Tray Icon
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
                
                if (File.Exists(iconFilePath)) {
                    trayIcon.Icon = new Icon(iconFilePath);
                } else if (File.Exists(iconPngPath)) {
                    using (var bmp = new Bitmap(iconPngPath)) {
                        IntPtr hIcon = bmp.GetHicon();
                        trayIcon.Icon = Icon.FromHandle(hIcon);
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
            trayIcon.ShowBalloonTip(
                2000, 
                "V Rec", 
                "App is running in system tray. Right-click the icon for options.", 
                Forms.ToolTipIcon.Info
            );
        }

        private void ExitApplication() {
            try {
                if (ffmpegProcess != null && !ffmpegProcess.HasExited) {
                    ffmpegProcess.Kill();
                }
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
            if (this.WindowState == WindowState.Minimized) {
                MinimizeToTray();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            var helper = new WindowInteropHelper(this);
            IntPtr hWnd = helper.Handle;
            
            RegisterHotKey(hWnd, HOTKEY_START, MOD_CONTROL | MOD_SHIFT, 0x52); // R
            RegisterHotKey(hWnd, HOTKEY_PAUSE, MOD_CONTROL | MOD_SHIFT, 0x50); // P
            RegisterHotKey(hWnd, HOTKEY_STOP, MOD_CONTROL | MOD_SHIFT, 0x53); // S
            RegisterHotKey(hWnd, HOTKEY_DRAW, MOD_CONTROL | MOD_SHIFT, 0x44); // D
            RegisterHotKey(hWnd, HOTKEY_SELECT_AREA, MOD_CONTROL | MOD_SHIFT, 0x41); // A
            
            HwndSource source = HwndSource.FromHwnd(hWnd);
            source.AddHook(HwndHook);
            
            Log("Global hotkeys registered");
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == WM_HOTKEY) {
                int hotkeyId = wParam.ToInt32();
                
                switch (hotkeyId) {
                    case HOTKEY_START:
                        Dispatcher.Invoke(() => StartRec_Click(null, null));
                        handled = true;
                        break;
                    case HOTKEY_PAUSE:
                        Dispatcher.Invoke(() => PauseRec_Click(null, null));
                        handled = true;
                        break;
                    case HOTKEY_STOP:
                        Dispatcher.Invoke(() => StopRec_Click(null, null));
                        handled = true;
                        break;
                    case HOTKEY_DRAW:
                        Dispatcher.Invoke(() => Draw_Click(null, null));
                        handled = true;
                        break;
                    case HOTKEY_SELECT_AREA:
                        Dispatcher.Invoke(() => SelectArea_Click(null, null));
                        handled = true;
                        break;
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
                if (trayIcon != null) {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            } catch { }
            
            base.OnClosed(e);
        }

        private void Log(string message) {
            try {
                string logEntry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + message + Environment.NewLine;
                File.AppendAllText(logFile, logEntry);
            } catch { }
        }

        private void UpdateTrayTooltip(string status) {
            try {
                if (trayIcon != null) {
                    trayIcon.Text = "V Rec - " + status;
                }
            } catch { }
        }

        private void CheckFFmpeg() {
            try {
                string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
                Log("Checking FFmpeg at: " + ffmpegPath);
                
                if (!File.Exists(ffmpegPath)) {
                    StatusText.Text = "Error: FFmpeg not found!";
                    Log("FFmpeg NOT FOUND");
                    UpdateTrayTooltip("FFmpeg Missing!");
                    MessageBox.Show("FFmpeg is missing from: " + appFolder, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                } else {
                    var fileInfo = new FileInfo(ffmpegPath);
                    Log("FFmpeg found. Size: " + fileInfo.Length + " bytes");
                    StatusText.Text = "Ready. Screen: " + screenWidth + "x" + screenHeight + ". Videos: " + outputFolder;
                    UpdateTrayTooltip("Ready");
                }
            } catch (Exception ex) {
                Log("CheckFFmpeg error: " + ex.Message);
                StatusText.Text = "Ready.";
            }
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
                Log("Opening area selection overlay");
                var areaSelector = new AreaSelectionOverlay();
                bool? result = areaSelector.ShowDialog();
                
                if (result == true && areaSelector.SelectedArea.HasValue) {
                    selectedArea = areaSelector.SelectedArea.Value;
                    Log("Area selected: " + selectedArea.Value);
                    
                    ResetAreaButton.Visibility = Visibility.Visible;
                    SelectAreaButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 144, 226));
                    
                    StatusText.Text = $"Area selected: {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height} at ({(int)selectedArea.Value.X},{(int)selectedArea.Value.Y})";
                    
                    trayIcon.ShowBalloonTip(
                        2000, 
                        "V Rec", 
                        $"Recording area set to {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}", 
                        Forms.ToolTipIcon.Info
                    );
                } else {
                    Log("Area selection cancelled");
                }
            } catch (Exception ex) {
                Log("SelectArea error: " + ex.Message);
                MessageBox.Show("Error selecting area: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetArea_Click(object sender, RoutedEventArgs e) {
            selectedArea = null;
            ResetAreaButton.Visibility = Visibility.Collapsed;
            SelectAreaButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 144, 226));
            StatusText.Text = "Recording full screen. Videos saved to: " + outputFolder;
            Log("Area reset to full screen");
        }

        private void StartRec_Click(object sender, RoutedEventArgs e) {
            string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) {
                MessageBox.Show("FFmpeg is missing. Cannot start recording.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (isRecording && !isPaused) return;
            
            if (!isRecording) {
                segmentCount = 0;
                try {
                    if (Directory.Exists(tempFolder)) {
                        try { Directory.Delete(tempFolder, true); } catch { }
                    }
                    Directory.CreateDirectory(tempFolder);
                    Log("Temp folder created: " + tempFolder);
                } catch (Exception ex) {
                    Log("Failed to create temp folder: " + ex.Message);
                    MessageBox.Show("Error creating temp folder: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            try {
                string outFile = Path.Combine(tempFolder, "seg_" + segmentCount + ".mp4");
                string args;
                
                if (selectedArea.HasValue) {
                    // Record selected area
                    int x = (int)selectedArea.Value.X;
                    int y = (int)selectedArea.Value.Y;
                    int width = (int)selectedArea.Value.Width;
                    int height = (int)selectedArea.Value.Height;
                    
                    args = "-y -f gdigrab -framerate 30 -video_size " + width + "x" + height + 
                           " -offset_x " + x + " -offset_y " + y +
                           " -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency " +
                           "-crf 23 -pix_fmt yuv420p \"" + outFile + "\"";
                    
                    Log("Recording selected area: " + width + "x" + height + " at (" + x + "," + y + ")");
                } else {
                    // Record full screen
                    args = "-y -f gdigrab -framerate 30 -video_size " + screenWidth + "x" + screenHeight + 
                           " -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency " +
                           "-crf 23 -pix_fmt yuv420p \"" + outFile + "\"";
                    
                    Log("Recording full screen: " + screenWidth + "x" + screenHeight);
                }
                
                ffmpegProcess = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = tempFolder
                    }
                };
                
                ffmpegProcess.ErrorDataReceived += (s, ev) => {
                    if (!string.IsNullOrEmpty(ev.Data)) Log("FFmpeg: " + ev.Data);
                };
                
                ffmpegProcess.Start();
                ffmpegProcess.BeginErrorReadLine();
                
                isRecording = true;
                isPaused = false;
                segmentCount++;
                Log("Recording started. Segment: " + segmentCount);
                clickOverlay.Start();
                
                StatusText.Text = selectedArea.HasValue ? 
                    $"Recording area: {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}..." : 
                    "Recording full screen...";
                UpdateTrayTooltip("🔴 Recording...");
                
                trayIcon.ShowBalloonTip(2000, "V Rec", "Recording started. UI hidden.", Forms.ToolTipIcon.Info);
                
                this.Hide();
                Log("Window hidden during recording");
            } catch (Exception ex) {
                Log("StartRec error: " + ex.Message + "\n" + ex.StackTrace);
                MessageBox.Show("Error starting recording: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PauseRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording || isPaused) return;
            isPaused = true;
            StopFFmpegGracefully();
            StatusText.Text = "Paused.";
            UpdateTrayTooltip("⏸️ Paused");
            Log("Recording paused");
            
            if (!this.IsVisible) {
                ShowWindow();
                trayIcon.ShowBalloonTip(2000, "V Rec", "Recording paused.", Forms.ToolTipIcon.Info);
            }
        }

        private void StopFFmpegGracefully() {
            try {
                if (ffmpegProcess != null && !ffmpegProcess.HasExited) {
                    Log("Stopping FFmpeg gracefully...");
                    try {
                        ffmpegProcess.StandardInput.WriteLine("q");
                        ffmpegProcess.StandardInput.Flush();
                    } catch (Exception ex) {
                        Log("Failed to send 'q' to FFmpeg: " + ex.Message);
                    }
                    
                    if (!ffmpegProcess.WaitForExit(5000)) {
                        Log("FFmpeg didn't exit in 5s, force killing");
                        try { ffmpegProcess.Kill(); } catch { }
                    } else {
                        Log("FFmpeg stopped gracefully");
                    }
                }
            } catch (Exception ex) {
                Log("StopFFmpeg error: " + ex.Message);
            }
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
                    Log("Starting video processing...");
                    string finalVideo = Path.Combine(tempFolder, "final_video.mp4");
                    
                    var segmentFiles = Directory.GetFiles(tempFolder, "seg_*.mp4");
                    Log("Found " + segmentFiles.Length + " segment files");
                    
                    var validSegments = segmentFiles.Where(f => new FileInfo(f).Length > 1000).ToArray();
                    Log("Valid segments: " + validSegments.Length);
                    
                    if (validSegments.Length == 0) {
                        throw new Exception("No valid video segments found. Check log: " + logFile);
                    }
                    
                    if (validSegments.Length > 1) {
                        string listFile = Path.Combine(tempFolder, "list.txt");
                        var files = validSegments.OrderBy(f => f);
                        File.WriteAllLines(listFile, files.Select(f => "file '" + Path.GetFullPath(f).Replace("'", "''") + "'"));
                        Log("Concatenating " + validSegments.Length + " segments");
                        
                        var concatProcess = Process.Start(new ProcessStartInfo {
                            FileName = ffmpegPath,
                            Arguments = "-y -f concat -safe 0 -i \"" + listFile + "\" -c copy \"" + finalVideo + "\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = tempFolder
                        });
                        concatProcess.WaitForExit();
                        Log("Concat exit code: " + concatProcess.ExitCode);
                        
                        if (concatProcess.ExitCode != 0 || !File.Exists(finalVideo)) {
                            throw new Exception("FFmpeg concat failed. Exit code: " + concatProcess.ExitCode);
                        }
                    } else {
                        File.Copy(validSegments[0], finalVideo, true);
                        Log("Single segment, copied to final_video.mp4");
                    }

                    if (!File.Exists(finalVideo) || new FileInfo(finalVideo).Length < 1000) {
                        throw new Exception("Final video file was not created properly");
                    }

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string finalFilename = Path.Combine(outputFolder, "VRec_" + timestamp + ".mp4");
                    Log("Output filename: " + finalFilename);

                    string bgPath = Path.Combine(appFolder, "bg.mp3");
                    if (File.Exists(bgPath)) {
                        Log("Mixing with background music: " + bgPath);
                        string mixArgs = "-y -i \"" + finalVideo + "\" -stream_loop -1 -i \"" + bgPath + 
                                         "\" -c:v copy -c:a aac -shortest -map 0:v:0 -map 1:a:0 \"" + finalFilename + "\"";
                        
                        var mixProcess = Process.Start(new ProcessStartInfo {
                            FileName = ffmpegPath,
                            Arguments = mixArgs,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = tempFolder
                        });
                        mixProcess.WaitForExit();
                        Log("Mix exit code: " + mixProcess.ExitCode);
                        
                        if (mixProcess.ExitCode != 0 || !File.Exists(finalFilename)) {
                            throw new Exception("FFmpeg audio mix failed. Exit code: " + mixProcess.ExitCode);
                        }
                        
                        try { File.Delete(finalVideo); } catch { }
                    } else {
                        Log("No bg.mp3 found, moving video without audio mix");
                        File.Move(finalVideo, finalFilename);
                    }

                    if (!File.Exists(finalFilename)) {
                        throw new Exception("Final output file was not created");
                    }
                    
                    Log("Video saved successfully: " + finalFilename);
                    
                    try {
                        if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
                        Log("Temp folder cleaned up");
                    } catch (Exception ex) {
                        Log("Failed to clean temp folder: " + ex.Message);
                    }
                    
                    Dispatcher.Invoke(() => {
                        trayIcon.ShowBalloonTip(
                            3000, 
                            "V Rec", 
                            "Video saved to: " + finalFilename, 
                            Forms.ToolTipIcon.Info
                        );
                    });
                } catch (Exception ex) {
                    Log("StopRec error: " + ex.Message + "\n" + ex.StackTrace);
                    Dispatcher.Invoke(() => {
                        trayIcon.ShowBalloonTip(
                            3000, 
                            "V Rec - Error", 
                            "Error: " + ex.Message, 
                            Forms.ToolTipIcon.Error
                        );
                    });
                }
            });

            StatusText.Text = selectedArea.HasValue ? 
                $"Ready. Area: {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}. Videos: {outputFolder}" : 
                "Ready. Videos saved to: " + outputFolder;
            UpdateTrayTooltip("Ready");
            StartRecButton.IsEnabled = true;
            isRecording = false;
            isPaused = false;
            
            if (!this.IsVisible) {
                ShowWindow();
            }
        }

        private void Draw_Click(object sender, RoutedEventArgs e) {
            try {
                if (overlay == null) overlay = new DrawingOverlay();
                overlay.Show();
                overlay.Activate();
                overlay.StartDrawing();
                drawTimer.Start();
                StatusText.Text = "Drawing mode. Hold left mouse. Press Esc to stop.";
                Log("Drawing mode activated");
            } catch (Exception ex) {
                Log("Draw_Click error: " + ex.Message);
                MessageBox.Show("Error starting draw mode: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e) {
            HelpPopup.IsOpen = !HelpPopup.IsOpen;
        }

        private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => MinimizeToTray();
        private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            this.DragMove();
        }
    }
}
