using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenRecorderApp {
    public partial class DrawingOverlay : Window {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        private bool isDrawing = false;
        private Point lastPos;
        private DispatcherTimer posTimer;
        private bool hasStartedDrawing = false;

        public DrawingOverlay() {
            InitializeComponent();
            this.Loaded += DrawingOverlay_Loaded;
        }

        private void DrawingOverlay_Loaded(object sender, RoutedEventArgs e) {
            // Ensure window is maximized and on top
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
        }

        public void StartDrawing() {
            isDrawing = true;
            hasStartedDrawing = false;
            ClickOverlay.IsSuppressed = true;
            
            posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            posTimer.Tick += PosTimer_Tick;
            posTimer.Start();
        }

        public void StopDrawing() {
            isDrawing = false;
            ClickOverlay.IsSuppressed = false;
            
            if (posTimer != null) {
                posTimer.Stop();
                posTimer.Tick -= PosTimer_Tick;
            }
            
            DrawLine.Points.Clear();
            hasStartedDrawing = false;
            this.Hide();
        }

        private void PosTimer_Tick(object sender, EventArgs e) {
            if (!isDrawing) return;

            try {
                bool isLeftClickHeld = (GetAsyncKeyState(0x01) & 0x8000) != 0;

                if (isLeftClickHeld) {
                    // Get mouse position relative to screen
                    var mousePos = Mouse.GetPosition(this);
                    Point screenPos = this.PointToScreen(mousePos);
                    
                    // Convert back to window coordinates
                    Point windowPos = new Point(screenPos.X - this.Left, screenPos.Y - this.Top);

                    if (!hasStartedDrawing) {
                        // First point - just set it
                        DrawLine.Points.Add(windowPos);
                        lastPos = windowPos;
                        hasStartedDrawing = true;
                    } else {
                        // Check if mouse moved enough (more than 2 pixels)
                        double dx = Math.Abs(lastPos.X - windowPos.X);
                        double dy = Math.Abs(lastPos.Y - windowPos.Y);
                        
                        if (dx > 2 || dy > 2) {
                            DrawLine.Points.Add(windowPos);
                            lastPos = windowPos;
                        }
                    }
                } else {
                    // Mouse button released - reset for next drawing session
                    hasStartedDrawing = false;
                    lastPos = default;
                }
            } catch {
                // Silently handle any coordinate conversion errors
            }
        }
    }
}
