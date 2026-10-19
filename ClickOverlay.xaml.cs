using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Media;

namespace ScreenRecorderApp {
    public partial class ClickOverlay : Window {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT {
            public int X;
            public int Y;
        }
        
        private DispatcherTimer timer;
        private bool wasLeftDown = false;
        private bool wasRightDown = false;
        private ClickSoundPlayer clickPlayer;

        public static bool IsSuppressed { get; set; } = false;

        public ClickOverlay() {
            InitializeComponent();
            this.Loaded += ClickOverlay_Loaded;
        }

        private void ClickOverlay_Loaded(object sender, RoutedEventArgs e) {
            // Force the window to cover the ENTIRE screen explicitly
            this.Left = 0;
            this.Top = 0;
            this.Width = GetSystemMetrics(0);  // SM_CXSCREEN
            this.Height = GetSystemMetrics(1); // SM_CYSCREEN
            this.WindowState = WindowState.Normal; // Use explicit size instead of Maximized
            this.Topmost = true;
            
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += Timer_Tick;
            clickPlayer = new ClickSoundPlayer();
        }

        public void Start() { 
            this.Show();
            this.Activate();
            // Ensure coverage after showing
            this.Left = 0;
            this.Top = 0;
            this.Width = GetSystemMetrics(0);
            this.Height = GetSystemMetrics(1);
            this.Topmost = true;
            if (timer != null) timer.Start(); 
        }
        
        public void Stop() { 
            if (timer != null) timer.Stop(); 
            ClickCanvas.Children.Clear(); 
            this.Hide(); 
        }

        private void Timer_Tick(object sender, EventArgs e) {
            if (IsSuppressed) return;

            try {
                bool leftDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                bool rightDown = (GetAsyncKeyState(0x02) & 0x8000) != 0;
                
                // Get actual cursor position
                GetCursorPos(out POINT cursorPos);
                
                // Since window covers entire screen at (0,0), use screen coords directly
                Point windowPos = new Point(cursorPos.X, cursorPos.Y);

                if (leftDown && !wasLeftDown) { 
                    SpawnRipple(windowPos, false); 
                    if (clickPlayer != null) clickPlayer.Play(); 
                }
                if (rightDown && !wasRightDown) { 
                    SpawnRipple(windowPos, true); 
                    if (clickPlayer != null) clickPlayer.Play(); 
                }

                wasLeftDown = leftDown;
                wasRightDown = rightDown;
            } catch { }
        }

        private void SpawnRipple(Point pos, bool isRightClick) {
            try {
                // Bigger, more visible ripple
                double size = 60;
                Color rippleColor = isRightClick ? Colors.Cyan : Colors.Red;
                
                Ellipse ripple = new Ellipse {
                    Stroke = new SolidColorBrush(rippleColor),
                    StrokeThickness = 4, 
                    Width = size, 
                    Height = size,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Opacity = 1.0
                };
                ripple.RenderTransform = new ScaleTransform(0.3, 0.3);
                
                Canvas.SetLeft(ripple, pos.X - size / 2);
                Canvas.SetTop(ripple, pos.Y - size / 2);
                ClickCanvas.Children.Add(ripple);

                // Smooth expanding animation - longer duration for better visibility
                var scaleX = new DoubleAnimation { 
                    From = 0.3, To = 2.0, 
                    Duration = TimeSpan.FromMilliseconds(600), 
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } 
                };
                var scaleY = new DoubleAnimation { 
                    From = 0.3, To = 2.0, 
                    Duration = TimeSpan.FromMilliseconds(600), 
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } 
                };
                var opacity = new DoubleAnimation { 
                    From = 1.0, To = 0.0, 
                    Duration = TimeSpan.FromMilliseconds(600) 
                };

                Storyboard.SetTarget(scaleX, ripple.RenderTransform);
                Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
                Storyboard.SetTarget(scaleY, ripple.RenderTransform);
                Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
                Storyboard.SetTarget(opacity, ripple);
                Storyboard.SetTargetProperty(opacity, new PropertyPath(UIElement.OpacityProperty));

                var sb = new Storyboard();
                sb.Children.Add(scaleX); 
                sb.Children.Add(scaleY); 
                sb.Children.Add(opacity);
                sb.Completed += (s, ev) => {
                    try { ClickCanvas.Children.Remove(ripple); } catch { }
                };
                sb.Begin();
            } catch { }
        }
    }

    public class ClickSoundPlayer {
        private SoundPlayer player;
        
        public ClickSoundPlayer() {
            try {
                string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrec_click.wav");
                if (!System.IO.File.Exists(tempWav)) GenerateClickWav(tempWav);
                player = new SoundPlayer(tempWav);
            } catch { }
        }
        
        public void Play() { 
            try { player?.Play(); } catch { } 
        }

        private void GenerateClickWav(string path) {
            int sampleRate = 22050, durationMs = 20, samples = sampleRate * durationMs / 1000;
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create)) {
                using (var writer = new System.IO.BinaryWriter(fs)) {
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                    writer.Write(36 + samples * 2);
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                    writer.Write(16); 
                    writer.Write((short)1); 
                    writer.Write((short)1);
                    writer.Write(sampleRate); 
                    writer.Write(sampleRate * 2);
                    writer.Write((short)2); 
                    writer.Write((short)16);
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                    writer.Write(samples * 2);
                    
                    for (int i = 0; i < samples; i++) {
                        double t = (double)i / sampleRate;
                        double amp = Math.Exp(-t * 150);
                        short val = (short)(Math.Sin(2 * Math.PI * 1500 * t) * amp * short.MaxValue * 0.8);
                        writer.Write(val);
                    }
                }
            }
        }
    }
}
