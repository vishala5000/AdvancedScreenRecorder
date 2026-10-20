using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        
        private int recordWidth;
        private int recordHeight;
        
        private Rect? selectedArea = null;
        private Forms.NotifyIcon trayIcon;
        private Forms.ContextMenuStrip trayMenu;

        public MainWindow() {
            InitializeComponent();
            
            appFolder = AppDomain.CurrentDomain.BaseDirectory;
            outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VRec");
            tempFolder = Path.Combine(Path.GetTempPath(), "VRec_Temp_" + Process.GetCurrentProcess().Id);
            
            screenWidth = GetSystemMetrics(0);
            screenHeight = GetSystemMetrics(1);
            
            try {
                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
            } catch {
                outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                try {
                    if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
                } catch {
                    outputFolder = appFolder;
                }
            }
            
            logFile = Path.Combine(outputFolder, "vrec_log.txt");
            
            string iconPath = Path.Combine(appFolder, "icon.png");
            if (File.Exists(iconPath)) {
                try { this.Icon = new BitmapImage(new Uri(iconPath)); } catch { }
            }

            InitializeTrayIcon();
            CheckFFmpeg();
            InitDrawTimer();
            clickOverlay = new ClickOverlay();
            
            this.Loaded += MainWindow_Loaded;
            this.StateChanged += MainWindow_StateChanged;
            
            Log("App started. Output folder: " + outputFolder);
        }

        private void InitializeTrayIcon() {
            try {
                trayMenu = new Forms.ContextMenuStrip();
                trayMenu.BackColor = System.Drawing.Color.FromArgb(26, 26, 26);
                trayMenu.ForeColor = System.Drawing.Color.White;
                
                var showItem = new Forms.ToolStripMenuItem("📺 Show V Rec");
                showItem.Click += (s, e) => ShowWindow();
                var startItem = new Forms.ToolStripMenuItem("▶ Start");
                startItem.Click += (s, e) => Dispatcher.Invoke(() => StartRec_Click(null, null));
                var pauseItem = new Forms.ToolStripMenuItem("⏸ Pause");
                pauseItem.Click += (s, e) => Dispatcher.Invoke(() => PauseRec_Click(null, null));
                var stopItem = new Forms.ToolStripMenuItem("⏹ Stop");
                stopItem.Click += (s, e) => Dispatcher.Invoke(() => StopRec_Click(null, null));
                var drawItem = new Forms.ToolStripMenuItem("✏️ Draw");
                drawItem.Click += (s, e) => Dispatcher.Invoke(() => Draw_Click(null, null));
                var areaItem = new Forms.ToolStripMenuItem("📐 Area");
                areaItem.Click += (s, e) => Dispatcher.Invoke(() => SelectArea_Click(null, null));
                var exitItem = new Forms.ToolStripMenuItem("❌ Exit");
                exitItem.Click += (s, e) => ExitApplication();
                
                trayMenu.Items.Add(showItem);
                trayMenu.Items.Add(new Forms.ToolStripSeparator());
                trayMenu.Items.Add(startItem);
                trayMenu.Items.Add(pauseItem);
                trayMenu.Items.Add(stopItem);
                trayMenu.Items.Add(drawItem);
                trayMenu.Items.Add(areaItem);
                trayMenu.Items.Add(new Forms.ToolStripSeparator());
                trayMenu.Items.Add(exitItem);
                
                trayIcon = new Forms.NotifyIcon { Visible = true, ContextMenuStrip = trayMenu, Text = "V Rec - Ready" };
                
                string iconFilePath = Path.Combine(appFolder, "icon.ico");
                string iconPngPath = Path.Combine(appFolder, "icon.png");
                
                if (File.Exists(iconFilePath)) {
                    trayIcon.Icon = new System.Drawing.Icon(iconFilePath);
                } else if (File.Exists(iconPngPath)) {
                    using (var bmp = new System.Drawing.Bitmap(iconPngPath)) {
                        trayIcon.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                    }
                } else {
                    trayIcon.Icon = System.Drawing.SystemIcons.Application;
                }
                
                trayIcon.DoubleClick += (s, e) => ShowWindow();
            } catch { }
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
            trayIcon?.ShowBalloonTip(2000, "V Rec", "Running in system tray.", Forms.ToolTipIcon.Info);
        }

        private void ExitApplication() {
            try { if (ffmpegProcess != null && !ffmpegProcess.HasExited) ffmpegProcess.Kill(); } catch { }
            try { if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); } } catch { }
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
            HwndSource.FromHwnd(hWnd).AddHook(HwndHook);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == WM_HOTKEY) {
                int id = wParam.ToInt32();
                if (id == HOTKEY_START) { Dispatcher.Invoke(() => StartRec_Click(null, null)); handled = true; }
                else if (id == HOTKEY_PAUSE) { Dispatcher.Invoke(() => PauseRec_Click(null, null)); handled = true; }
                else if (id == HOTKEY_STOP) { Dispatcher.Invoke(() => StopRec_Click(null, null)); handled = true; }
                else if (id == HOTKEY_DRAW) { Dispatcher.Invoke(() => Draw_Click(null, null)); handled = true; }
                else if (id == HOTKEY_SELECT_AREA) { Dispatcher.Invoke(() => SelectArea_Click(null, null)); handled = true; }
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
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            } catch { }
            base.OnClosed(e);
        }

        private void Log(string msg) {
            try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + msg + Environment.NewLine); } catch { }
        }

        private void CheckFFmpeg() {
            string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) {
                StatusText.Text = "Error: FFmpeg missing!";
                MessageBox.Show("FFmpeg is missing from: " + appFolder, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            } else {
                StatusText.Text = "Ready";
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
                var areaSelector = new AreaSelectionOverlay();
                if (areaSelector.ShowDialog() == true && areaSelector.SelectedArea.HasValue) {
                    selectedArea = areaSelector.SelectedArea.Value;
                    ResetAreaButton.Visibility = Visibility.Visible;
                    StatusText.Text = $"Area: {(int)selectedArea.Value.Width}x{(int)selectedArea.Value.Height}";
                    Log("Area selected: " + selectedArea.Value);
                }
            } catch (Exception ex) {
                Log("SelectArea error: " + ex.Message);
                MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetArea_Click(object sender, RoutedEventArgs e) {
            selectedArea = null;
            ResetAreaButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Ready (Full Screen)";
            Log("Area reset to full screen");
        }

        private void StartRec_Click(object sender, RoutedEventArgs e) {
            string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) { MessageBox.Show("FFmpeg missing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            if (isRecording && !isPaused) return;

            if (!isRecording) {
                segmentCount = 0;
                try {
                    if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
                    Directory.CreateDirectory(tempFolder);
                } catch (Exception ex) {
                    Log("Temp folder error: " + ex.Message);
                    MessageBox.Show("Temp folder error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            try {
                string outFile = Path.Combine(tempFolder, "seg_" + segmentCount + ".mp4");
                
                if (selectedArea.HasValue) {
                    recordWidth = (int)selectedArea.Value.Width;
                    recordHeight = (int)selectedArea.Value.Height;
                    if (recordWidth % 2 != 0) recordWidth -= 1;
                    if (recordHeight % 2 != 0) recordHeight -= 1;
                } else {
                    recordWidth = screenWidth;
                    recordHeight = screenHeight;
                }

                int offsetX = selectedArea.HasValue ? (int)selectedArea.Value.X : 0;
                int offsetY = selectedArea.HasValue ? (int)selectedArea.Value.Y : 0;

                string args = $"-y -f gdigrab -framerate 30 -video_size {recordWidth}x{recordHeight} -offset_x {offsetX} -offset_y {offsetY} -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p \"{outFile}\"";
                
                Log("Starting FFmpeg: " + args);
                
                ffmpegProcess = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = ffmpegPath, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardError = true, WorkingDirectory = tempFolder
                    }
                };
                
                ffmpegProcess.ErrorDataReceived += (s, ev) => { if (!string.IsNullOrEmpty(ev.Data)) Log("FFmpeg: " + ev.Data); };
                ffmpegProcess.Start();
                ffmpegProcess.BeginErrorReadLine();
                
                Thread.Sleep(300);
                
                if (ffmpegProcess.HasExited) {
                    Log("FFmpeg exited immediately with code: " + ffmpegProcess.ExitCode);
                    throw new Exception("FFmpeg exited immediately. Check log for details.");
                }

                isRecording = true;
                isPaused = false;
                segmentCount++;
                clickOverlay.Start();
                StatusText.Text = "🔴 Recording...";
                this.Hide();
                Log("Recording started successfully");
            } catch (Exception ex) {
                Log("Start Error: " + ex.Message);
                MessageBox.Show("Recording failed: " + ex.Message + "\n\nLog: " + logFile, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Show();
            }
        }

        private void PauseRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording || isPaused) return;
            isPaused = true;
            StopFFmpegGracefully();
            StatusText.Text = "⏸ Paused";
            Log("Recording paused");
            if (!this.IsVisible) ShowWindow();
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
            isRecording = false;
            isPaused = false;
            StartRecButton.IsEnabled = false;

            string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
            StopFFmpegGracefully();
            clickOverlay.Stop();

            StatusText.Text = "⚙️ Processing & Adding Outro...";
            
            await Task.Run(() => {
                try {
                    Log("Starting video processing");
                    string finalVideo = Path.Combine(tempFolder, "final_video.mp4");
                    var validSegments = Directory.GetFiles(tempFolder, "seg_*.mp4").Where(f => new FileInfo(f).Length > 0).ToArray();
                    
                    Log("Found " + validSegments.Length + " segments");
                    
                    if (validSegments.Length == 0) throw new Exception("No video data recorded.");
                    
                    // 1. Concatenate segments
                    if (validSegments.Length > 1) {
                        string listFile = Path.Combine(tempFolder, "list.txt");
                        File.WriteAllLines(listFile, validSegments.OrderBy(f => f).Select(f => "file '" + Path.GetFullPath(f).Replace("'", "''") + "'"));
                        var concat = Process.Start(new ProcessStartInfo { FileName = ffmpegPath, Arguments = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{finalVideo}\"", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tempFolder });
                        concat.WaitForExit();
                        if (concat.ExitCode != 0 || !File.Exists(finalVideo)) throw new Exception("Concat failed.");
                    } else {
                        File.Copy(validSegments[0], finalVideo, true);
                    }

                    // 2. Generate Engaging 7-Second Outro
                    Log("Generating 7-second animated outro...");
                    string outroFile = Path.Combine(tempFolder, "outro.mp4");
                    
                    // Dynamically calculate font size based on video height
                    int calcSize1 = recordHeight / 10;
                    int calcSize2 = recordHeight / 16;
                    int fontSize1 = Math.Max(16, Math.Min(120, calcSize1));
                    int fontSize2 = Math.Max(12, Math.Min(80, calcSize2));
                    
                    // Copy font to temp folder so FFmpeg can find it reliably
                    string tempFontPath = Path.Combine(tempFolder, "font.ttf");
                    string appFontPath = Path.Combine(appFolder, "font.ttf");
                    if (File.Exists(appFontPath)) {
                        File.Copy(appFontPath, tempFontPath, true);
                    }
                    
                    // Use temp font, or fallback to arial
                    string fontArg = File.Exists(tempFontPath) ? "fontfile=font.ttf:" : "fontfile=arial:";
                    
                    // CRITICAL FIX: Removed text_wrap parameters that were causing "Option not found" errors in some FFmpeg builds.
                    // The text is short enough that it will fit perfectly centered without wrapping in almost all resolutions.
                    string outroArgs = $"-y -f lavfi -i color=c=black:s={recordWidth}x{recordHeight}:d=7 " +
                                       $"-vf \"drawtext={fontArg}text='Like, Share and Subscribe':fontcolor=#E60000:fontsize={fontSize1}:x=(w-text_w)/2:y=(h-text_h)/2-20:enable='between(t,0.5,7)'," +
                                       $"drawtext={fontArg}text='For More Videos!':fontcolor=white:fontsize={fontSize2}:x=(w-text_w)/2:y=(h-text_h)/2+30:enable='between(t,1.5,7)'\" " +
                                       $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{outroFile}\"";
                    
                    var outroProcess = Process.Start(new ProcessStartInfo {
                        FileName = ffmpegPath,
                        Arguments = outroArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        WorkingDirectory = tempFolder
                    });
                    
                    string outroError = outroProcess.StandardError.ReadToEnd();
                    outroProcess.WaitForExit();

                    // 3. Append Outro to Main Video
                    if (outroProcess.ExitCode == 0 && File.Exists(outroFile)) {
                        Log("Outro generated successfully. Appending to video...");
                        string listOutro = Path.Combine(tempFolder, "list_outro.txt");
                        string finalWithOutro = Path.Combine(tempFolder, "final_with_outro.mp4");
                        
                        File.WriteAllLines(listOutro, new[] {
                            $"file '{Path.GetFullPath(finalVideo).Replace("'", "''")}'",
                            $"file '{Path.GetFullPath(outroFile).Replace("'", "''")}'"
                        });
                        
                        var concatOutro = Process.Start(new ProcessStartInfo {
                            FileName = ffmpegPath,
                            Arguments = $"-y -f concat -safe 0 -i \"{listOutro}\" -c copy \"{finalWithOutro}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = tempFolder
                        });
                        concatOutro.WaitForExit();
                        
                        if (concatOutro.ExitCode == 0 && File.Exists(finalWithOutro)) {
                            File.Delete(finalVideo);
                            File.Move(finalWithOutro, finalVideo);
                            Log("Outro appended successfully.");
                        } else {
                            Log("Outro append failed, proceeding with original video.");
                        }
                    } else {
                        Log("Outro generation failed with code " + outroProcess.ExitCode + ": " + outroError);
                    }

                    // 4. Mix Background Music (if exists)
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string finalFilename = Path.Combine(outputFolder, "VRec_" + timestamp + ".mp4");
                    string bgPath = Path.Combine(appFolder, "bg.mp3");

                    if (File.Exists(bgPath)) {
                        Log("Mixing with background music...");
                        var mix = Process.Start(new ProcessStartInfo { 
                            FileName = ffmpegPath, 
                            Arguments = $"-y -i \"{finalVideo}\" -stream_loop -1 -i \"{bgPath}\" -c:v copy -c:a aac -shortest -map 0:v:0 -map 1:a:0 \"{finalFilename}\"", 
                            UseShellExecute = false, 
                            CreateNoWindow = true, 
                            WorkingDirectory = tempFolder 
                        });
                        mix.WaitForExit();
                        
                        if (mix.ExitCode != 0 || !File.Exists(finalFilename)) throw new Exception("Audio mix failed.");
                        try { File.Delete(finalVideo); } catch { }
                    } else {
                        File.Move(finalVideo, finalFilename);
                    }

                    if (!File.Exists(finalFilename)) throw new Exception("Final file not created.");
                    
                    try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
                    
                    Log("Video saved: " + finalFilename);
                    
                    Dispatcher.Invoke(() => {
                        trayIcon?.ShowBalloonTip(3000, "V Rec", "Saved: " + finalFilename, Forms.ToolTipIcon.Info);
                        MessageBox.Show("Video saved successfully!\n\n" + finalFilename, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                } catch (Exception ex) {
                    Log("Stop Error: " + ex.Message);
                    Dispatcher.Invoke(() => {
                        this.Show();
                        MessageBox.Show("Save failed: " + ex.Message + "\n\nLog: " + logFile, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            });

            StatusText.Text = "Ready";
            StartRecButton.IsEnabled = true;
            if (!this.IsVisible) ShowWindow();
        }

        private void Draw_Click(object sender, RoutedEventArgs e) {
            try {
                if (overlay == null) overlay = new DrawingOverlay();
                overlay.Show(); overlay.Activate(); overlay.StartDrawing(); drawTimer.Start();
                StatusText.Text = "✏️ Drawing (Esc to stop)";
                Log("Draw mode activated");
            } catch (Exception ex) {
                Log("Draw error: " + ex.Message);
                MessageBox.Show("Draw error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e) {
            MessageBox.Show(
                "Keyboard Shortcuts:\n\n" +
                "Ctrl + Shift + R  →  Start Recording\n" +
                "Ctrl + Shift + P  →  Pause Recording\n" +
                "Ctrl + Shift + S  →  Stop Recording\n" +
                "Ctrl + Shift + D  →  Toggle Draw Mode\n" +
                "Ctrl + Shift + A  →  Select Area\n" +
                "Esc               →  Stop Drawing\n\n" +
                "Log file location:\n" + logFile,
                "V Rec Help", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => MinimizeToTray();
        private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => this.DragMove();
    }
}
