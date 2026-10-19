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

            CheckFFmpeg();
            InitDrawTimer();
            clickOverlay = new ClickOverlay();
            
            this.Loaded += MainWindow_Loaded;
            
            Log("MainWindow initialized. Screen: " + screenWidth + "x" + screenHeight);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            var helper = new WindowInteropHelper(this);
            IntPtr hWnd = helper.Handle;
            
            RegisterHotKey(hWnd, HOTKEY_START, MOD_CONTROL | MOD_SHIFT, 0x52); // R
            RegisterHotKey(hWnd, HOTKEY_PAUSE, MOD_CONTROL | MOD_SHIFT, 0x50); // P
            RegisterHotKey(hWnd, HOTKEY_STOP, MOD_CONTROL | MOD_SHIFT, 0x53); // S
            RegisterHotKey(hWnd, HOTKEY_DRAW, MOD_CONTROL | MOD_SHIFT, 0x44); // D
            
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
                }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e) {
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_START);
            UnregisterHotKey(helper.Handle, HOTKEY_PAUSE);
            UnregisterHotKey(helper.Handle, HOTKEY_STOP);
            UnregisterHotKey(helper.Handle, HOTKEY_DRAW);
            
            base.OnClosed(e);
        }

        private void Log(string message) {
            try {
                string logEntry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + message + Environment.NewLine;
                File.AppendAllText(logFile, logEntry);
            } catch { }
        }

        private void CheckFFmpeg() {
            try {
                string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
                Log("Checking FFmpeg at: " + ffmpegPath);
                
                if (!File.Exists(ffmpegPath)) {
                    StatusText.Text = "Error: FFmpeg not found!";
                    Log("FFmpeg NOT FOUND");
                    MessageBox.Show("FFmpeg is missing from: " + appFolder, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                } else {
                    var fileInfo = new FileInfo(ffmpegPath);
                    Log("FFmpeg found. Size: " + fileInfo.Length + " bytes");
                    StatusText.Text = "Ready. Screen: " + screenWidth + "x" + screenHeight + ". Videos: " + outputFolder;
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
                string args = "-y -f gdigrab -framerate 30 -video_size " + screenWidth + "x" + screenHeight + 
                              " -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency " +
                              "-crf 23 -pix_fmt yuv420p \"" + outFile + "\"";
                
                Log("Starting FFmpeg with args: " + args);
                
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
                
                // HIDE THE WINDOW SO IT DOESN'T APPEAR IN THE VIDEO
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
            Log("Recording paused");
            
            // Show window when paused so user can see status
            if (!this.IsVisible) {
                this.Show();
                this.Activate();
                StatusText.Text = "Paused. Use Ctrl+Shift+R to resume or Ctrl+Shift+S to stop.";
                Log("Window shown during pause");
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

            Log("Starting video processing...");
            StartRecButton.IsEnabled = false;
            
            await Task.Run(() => {
                try {
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
                        MessageBox.Show("Video saved successfully!\n\nLocation: " + finalFilename, 
                                        "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                } catch (Exception ex) {
                    Log("StopRec error: " + ex.Message + "\n" + ex.StackTrace);
                    Dispatcher.Invoke(() => {
                        MessageBox.Show("Error saving video: " + ex.Message + "\n\nCheck log: " + logFile, 
                                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            });

            StatusText.Text = "Ready. Videos saved to: " + outputFolder;
            StartRecButton.IsEnabled = true;
            isRecording = false;
            isPaused = false;
            
            // SHOW THE WINDOW AGAIN AFTER RECORDING STOPS
            if (!this.IsVisible) {
                this.Show();
                this.Activate();
                Log("Window shown after recording stopped");
            }
        }

        private void Draw_Click(object sender, RoutedEventArgs e) {
            try {
                if (overlay == null) overlay = new DrawingOverlay();
                overlay.Show();
                overlay.Activate();
                overlay.StartDrawing();
                drawTimer.Start();
                Log("Drawing mode activated");
            } catch (Exception ex) {
                Log("Draw_Click error: " + ex.Message);
                MessageBox.Show("Error starting draw mode: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            this.DragMove();
        }
    }
}
