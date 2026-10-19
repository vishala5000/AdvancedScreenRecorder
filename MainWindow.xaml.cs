using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenRecorderApp {
    public partial class MainWindow : Window {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        private const int VK_ESCAPE = 0x1B;

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
        private int screenWidth;
        private int screenHeight;

        public MainWindow() {
            InitializeComponent();
            
            appFolder = AppDomain.CurrentDomain.BaseDirectory;
            outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VRec");
            tempFolder = Path.Combine(Path.GetTempPath(), "VRec_Temp");
            
            // Get actual screen resolution in pixels
            screenWidth = GetSystemMetrics(0);
            screenHeight = GetSystemMetrics(1);
            
            try {
                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
            } catch {
                outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }
            
            string iconPath = Path.Combine(appFolder, "icon.png");
            if (File.Exists(iconPath)) {
                try { this.Icon = new BitmapImage(new Uri(iconPath)); } catch { }
            }

            CheckFFmpeg();
            InitDrawTimer();
            clickOverlay = new ClickOverlay();
        }

        private void CheckFFmpeg() {
            try {
                string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
                if (!File.Exists(ffmpegPath)) {
                    StatusText.Text = "Error: FFmpeg not found!";
                    MessageBox.Show("FFmpeg is missing from: " + appFolder, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                } else {
                    StatusText.Text = "Ready. Screen: " + screenWidth + "x" + screenHeight + ". Videos saved to: " + outputFolder;
                }
            } catch {
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
                } catch (Exception ex) {
                    MessageBox.Show("Error creating temp folder: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            try {
                string outFile = Path.Combine(tempFolder, "seg_" + segmentCount + ".mp4");
                // Use actual screen resolution + fragmented MP4 for safe recording
                string args = "-y -f gdigrab -framerate 30 -video_size " + screenWidth + "x" + screenHeight + 
                              " -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency " +
                              "-crf 23 -pix_fmt yuv420p -movflags +frag_keyframe+empty_moov+default_base_moof \"" + outFile + "\"";
                
                ffmpegProcess = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        WorkingDirectory = tempFolder
                    }
                };
                
                ffmpegProcess.Start();
                
                isRecording = true;
                isPaused = false;
                segmentCount++;
                StatusText.Text = "Recording " + screenWidth + "x" + screenHeight + "...";
                clickOverlay.Start();
            } catch (Exception ex) {
                MessageBox.Show("Error starting recording: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error starting recording";
            }
        }

        private void PauseRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording || isPaused) return;
            isPaused = true;
            StopFFmpegGracefully();
            StatusText.Text = "Paused.";
        }

        private void StopFFmpegGracefully() {
            try {
                if (ffmpegProcess != null && !ffmpegProcess.HasExited) {
                    // Send 'q' command to FFmpeg to gracefully stop (prevents corrupted files)
                    try {
                        ffmpegProcess.StandardInput.WriteLine("q");
                        ffmpegProcess.StandardInput.Flush();
                    } catch { }
                    
                    if (!ffmpegProcess.WaitForExit(5000)) {
                        // If it didn't exit in 5 seconds, force kill
                        try { ffmpegProcess.Kill(); } catch { }
                    }
                }
            } catch { }
        }

        private async void StopRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording) return;
            
            string ffmpegPath = Path.Combine(appFolder, "ffmpeg.exe");
            StopFFmpegGracefully();
            clickOverlay.Stop();

            StatusText.Text = "Processing & Mixing Audio... Please wait.";
            StartRecButton.IsEnabled = false;
            
            await Task.Run(() => {
                try {
                    string finalVideo = Path.Combine(tempFolder, "final_video.mp4");
                    
                    var segmentFiles = Directory.GetFiles(tempFolder, "seg_*.mp4")
                        .Where(f => new FileInfo(f).Length > 1000) // Only valid files
                        .ToArray();
                    
                    if (segmentFiles.Length == 0) {
                        throw new Exception("No valid video segments found. Recording may have been too short.");
                    }
                    
                    if (segmentFiles.Length > 1) {
                        string listFile = Path.Combine(tempFolder, "list.txt");
                        var files = segmentFiles.OrderBy(f => f);
                        File.WriteAllLines(listFile, files.Select(f => "file '" + Path.GetFullPath(f).Replace("'", "''") + "'"));
                        
                        var concatProcess = Process.Start(new ProcessStartInfo {
                            FileName = ffmpegPath,
                            Arguments = "-y -f concat -safe 0 -i \"" + listFile + "\" -c copy \"" + finalVideo + "\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = tempFolder
                        });
                        concatProcess.WaitForExit();
                        
                        if (concatProcess.ExitCode != 0 || !File.Exists(finalVideo)) {
                            throw new Exception("FFmpeg concat failed.");
                        }
                    } else {
                        File.Copy(segmentFiles[0], finalVideo, true);
                    }

                    if (!File.Exists(finalVideo) || new FileInfo(finalVideo).Length < 1000) {
                        throw new Exception("Final video file was not created properly.");
                    }

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string finalFilename = Path.Combine(outputFolder, "VRec_" + timestamp + ".mp4");

                    string bgPath = Path.Combine(appFolder, "bg.mp3");
                    if (File.Exists(bgPath)) {
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
                        
                        if (mixProcess.ExitCode != 0 || !File.Exists(finalFilename)) {
                            throw new Exception("FFmpeg audio mix failed.");
                        }
                        
                        try { File.Delete(finalVideo); } catch { }
                    } else {
                        File.Move(finalVideo, finalFilename);
                    }

                    if (!File.Exists(finalFilename)) {
                        throw new Exception("Final output file was not created.");
                    }
                    
                    try {
                        if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
                    } catch { }
                    
                    Dispatcher.Invoke(() => {
                        MessageBox.Show("Video saved successfully!\n\nLocation: " + finalFilename, 
                                        "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                } catch (Exception ex) {
                    Dispatcher.Invoke(() => {
                        MessageBox.Show("Error saving video: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            });

            StatusText.Text = "Ready. Videos saved to: " + outputFolder;
            StartRecButton.IsEnabled = true;
            isRecording = false;
            isPaused = false;
        }

        private void Draw_Click(object sender, RoutedEventArgs e) {
            try {
                if (overlay == null) overlay = new DrawingOverlay();
                overlay.Show();
                overlay.Activate();
                overlay.StartDrawing();
                drawTimer.Start();
                StatusText.Text = "Drawing mode active. Hold left mouse to draw. Press Esc to stop.";
            } catch (Exception ex) {
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
