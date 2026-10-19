using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenRecorderApp {
    public partial class MainWindow : Window {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private const int VK_ESCAPE = 0x1B;

        private Process ffmpegProcess;
        private bool isRecording = false;
        private bool isPaused = false;
        private int segmentCount = 0;
        private DrawingOverlay overlay;
        private ClickOverlay clickOverlay;
        private DispatcherTimer drawTimer;

        public MainWindow() {
            InitializeComponent();
            EnsureFFmpeg();
            InitDrawTimer();
            clickOverlay = new ClickOverlay();
        }

        private void EnsureFFmpeg() {
            if (!File.Exists("ffmpeg.exe")) {
                StatusText.Text = "Downloading FFmpeg...";
                using var client = new WebClient();
                client.DownloadFile("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", "ffmpeg.zip");
                System.IO.Compression.ZipFile.ExtractToDirectory("ffmpeg.zip", "ffmpeg_temp");
                var exe = Directory.GetFiles("ffmpeg_temp", "ffmpeg.exe", SearchOption.AllDirectories)[0];
                File.Copy(exe, "ffmpeg.exe");
                Directory.Delete("ffmpeg_temp", true);
                File.Delete("ffmpeg.zip");
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
            if (isRecording && !isPaused) return;
            if (!isRecording) {
                segmentCount = 0;
                if (Directory.Exists("temp_segments")) Directory.Delete("temp_segments", true);
                Directory.CreateDirectory("temp_segments");
            }

            string outFile = System.IO.Path.Combine("temp_segments", "seg_" + segmentCount + ".mp4");
            string args = "-f gdigrab -framerate 30 -video_size 1920x1080 -draw_mouse 1 -i desktop -c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -pix_fmt yuv420p \"" + outFile + "\"";
            
            ffmpegProcess = new Process {
                StartInfo = new ProcessStartInfo("ffmpeg.exe", args) { UseShellExecute = false, CreateNoWindow = true }
            };
            ffmpegProcess.Start();
            
            isRecording = true;
            isPaused = false;
            segmentCount++;
            StatusText.Text = "Recording 1080p...";
            clickOverlay.Start();
        }

        private void PauseRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording || isPaused) return;
            isPaused = true;
            if (ffmpegProcess != null && !ffmpegProcess.HasExited) ffmpegProcess.Kill();
            StatusText.Text = "Paused.";
        }

        private async void StopRec_Click(object sender, RoutedEventArgs e) {
            if (!isRecording) return;
            if (ffmpegProcess != null && !ffmpegProcess.HasExited) ffmpegProcess.Kill();
            clickOverlay.Stop();

            StatusText.Text = "Processing & Mixing Audio... Please wait.";
            StartRecButton.IsEnabled = false;
            
            await Task.Run(() => {
                string finalVideo = System.IO.Path.Combine("temp_segments", "final_video.mp4");
                
                if (segmentCount > 1) {
                    string listFile = System.IO.Path.Combine("temp_segments", "list.txt");
                    var files = Directory.GetFiles("temp_segments", "seg_*.mp4").OrderBy(f => f);
                    File.WriteAllLines(listFile, files.Select(f => "file '" + System.IO.Path.GetFullPath(f) + "'"));
                    Process.Start("ffmpeg.exe", "-f concat -safe 0 -i \"" + listFile + "\" -c copy \"" + finalVideo + "\"").WaitForExit();
                } else {
                    File.Move(System.IO.Path.Combine("temp_segments", "seg_0.mp4"), finalVideo);
                }

                if (File.Exists("bg.mp3")) {
                    string mixArgs = "-i \"" + finalVideo + "\" -stream_loop -1 -i bg.mp3 -c:v copy -c:a aac -shortest -map 0:v:0 -map 1:a:0 \"Final_Recording.mp4\"";
                    Process.Start("ffmpeg.exe", mixArgs).WaitForExit();
                    File.Delete(finalVideo);
                } else {
                    File.Move(finalVideo, "Final_Recording.mp4");
                }
            });

            StatusText.Text = "Saved: Final_Recording.mp4";
            StartRecButton.IsEnabled = true;
            isRecording = false;
            isPaused = false;
        }

        private void Draw_Click(object sender, RoutedEventArgs e) {
            if (overlay == null) overlay = new DrawingOverlay();
            overlay.Show();
            overlay.StartDrawing();
            drawTimer.Start();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    }
}
