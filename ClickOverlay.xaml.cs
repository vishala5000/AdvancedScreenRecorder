using System;
using System.IO;
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
        
        private DispatcherTimer timer;
        private bool wasLeftDown = false;
        private bool wasRightDown = false;
        private ClickSoundPlayer clickPlayer;

        public ClickOverlay() {
            InitializeComponent();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += Timer_Tick;
            
            // Initialize the auto-generated click sound
            clickPlayer = new ClickSoundPlayer();
        }

        public void Start() {
            this.Show();
            timer.Start();
        }

        public void Stop() {
            timer.Stop();
            ClickCanvas.Children.Clear();
            this.Hide();
        }

        private void Timer_Tick(object sender, EventArgs e) {
            bool leftDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool rightDown = (GetAsyncKeyState(0x02) & 0x8000) != 0;

            var pos = PointToScreen(Mouse.GetPosition(this));

            if (leftDown && !wasLeftDown) {
                SpawnRipple(pos, false);
                clickPlayer.Play();
            }
            if (rightDown && !wasRightDown) {
                SpawnRipple(pos, true);
                clickPlayer.Play();
            }

            wasLeftDown = leftDown;
            wasRightDown = rightDown;
        }

        private void SpawnRipple(Point pos, bool isRightClick) {
            double size = 40;
            Ellipse ripple = new Ellipse {
                Stroke = isRightClick ? Brushes.Cyan : Brushes.Red,
                StrokeThickness = 3,
                Width = size,
                Height = size,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            
            ripple.RenderTransform = new ScaleTransform(0.1, 0.1);
            
            Canvas.SetLeft(ripple, pos.X - size / 2);
            Canvas.SetTop(ripple, pos.Y - size / 2);
            ClickCanvas.Children.Add(ripple);

            var scaleXAnim = new DoubleAnimation { From = 0.1, To = 1.5, Duration = TimeSpan.FromSeconds(0.4), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var scaleYAnim = new DoubleAnimation { From = 0.1, To = 1.5, Duration = TimeSpan.FromSeconds(0.4), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var opacityAnim = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromSeconds(0.4) };

            Storyboard.SetTarget(scaleXAnim, ripple.RenderTransform);
            Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath(ScaleTransform.ScaleXProperty));
            Storyboard.SetTarget(scaleYAnim, ripple.RenderTransform);
            Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath(ScaleTransform.ScaleYProperty));
            Storyboard.SetTarget(opacityAnim, ripple);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));

            var sb = new Storyboard();
            sb.Children.Add(scaleXAnim);
            sb.Children.Add(scaleYAnim);
            sb.Children.Add(opacityAnim);
            
            sb.Completed += (s, ev) => ClickCanvas.Children.Remove(ripple);
            sb.Begin();
        }
    }

    // ==========================================
    // AUTO-GENERATED CLICK SOUND ENGINE
    // ==========================================
    public class ClickSoundPlayer {
        private SoundPlayer player;

        public ClickSoundPlayer() {
            // Fully qualified to prevent conflict with System.Windows.Shapes.Path
            string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrec_click.wav");
            if (!System.IO.File.Exists(tempWav)) {
                GenerateClickWav(tempWav);
            }
            player = new SoundPlayer(tempWav);
        }

        public void Play() {
            try { player.Play(); } catch { }
        }

        private void GenerateClickWav(string path) {
            int sampleRate = 22050;
            int durationMs = 20;
            int samples = sampleRate * durationMs / 1000;
            
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create)) {
                using (var writer = new System.IO.BinaryWriter(fs)) {
                    // Write standard WAV header
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                    writer.Write(36 + samples * 2);
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                    writer.Write(16);
                    writer.Write((short)1); // PCM
                    writer.Write((short)1); // Mono
                    writer.Write(sampleRate);
                    writer.Write(sampleRate * 2);
                    writer.Write((short)2);
                    writer.Write((short)16);
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                    writer.Write(samples * 2);
                    
                    // Mathematically synthesize a crisp, high-frequency click burst
                    for (int i = 0; i < samples; i++) {
                        double t = (double)i / sampleRate;
                        double freq = 1500;
                        double amp = Math.Exp(-t * 150); // Fast decay for a "click" feel
                        short val = (short)(Math.Sin(2 * Math.PI * freq * t) * amp * short.MaxValue * 0.8);
                        writer.Write(val);
                    }
                }
            }
        }
    }
}
