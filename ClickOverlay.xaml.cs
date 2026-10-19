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
        
        private DispatcherTimer timer;
        private bool wasLeftDown = false;
        private bool wasRightDown = false;
        private SoundPlayer clickSound;

        public ClickOverlay() {
            InitializeComponent();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // 60fps polling
            timer.Tick += Timer_Tick;
            
            if (System.IO.File.Exists("click.wav")) {
                clickSound = new SoundPlayer("click.wav");
            }
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
                try { clickSound?.Play(); } catch { }
            }
            if (rightDown && !wasRightDown) {
                SpawnRipple(pos, true);
                try { clickSound?.Play(); } catch { }
            }

            wasLeftDown = leftDown;
            wasRightDown = rightDown;
        }

        private void SpawnRipple(Point pos, bool isRightClick) {
            double size = 40;
            Ellipse ripple = new Ellipse {
                Stroke = isRightClick ? Brushes.Cyan : Brushes.Red, // Cyan for Right, Red for Left
                StrokeThickness = 3,
                Width = size,
                Height = size,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            
            ripple.RenderTransform = new ScaleTransform(0.1, 0.1);
            
            Canvas.SetLeft(ripple, pos.X - size / 2);
            Canvas.SetTop(ripple, pos.Y - size / 2);
            ClickCanvas.Children.Add(ripple);

            // Smooth expanding and fading animation
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
            
            sb.Completed += (s, ev) => ClickCanvas.Children.Remove(ripple); // Clean up to save memory
            sb.Begin();
        }
    }
}
