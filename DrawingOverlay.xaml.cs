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

        public DrawingOverlay() {
            InitializeComponent();
            posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            posTimer.Tick += PosTimer_Tick;
        }

        public void StartDrawing() {
            isDrawing = true;
            // Suppress click detection while drawing
            ClickOverlay.IsSuppressed = true;
            posTimer.Start();
        }

        public void StopDrawing() {
            isDrawing = false;
            // Re-enable click detection
            ClickOverlay.IsSuppressed = false;
            posTimer.Stop();
            DrawLine.Points.Clear();
            this.Hide();
        }

        private void PosTimer_Tick(object sender, EventArgs e) {
            if (!isDrawing) return;

            bool isLeftClickHeld = (GetAsyncKeyState(0x01) & 0x8000) != 0;

            if (isLeftClickHeld) {
                var pos = PointToScreen(Mouse.GetPosition(this));
                Point currentPos = new Point(pos.X, pos.Y);

                if (lastPos != default && (Math.Abs(lastPos.X - currentPos.X) > 2 || Math.Abs(lastPos.Y - currentPos.Y) > 2)) {
                    DrawLine.Points.Add(currentPos);
                }
                lastPos = currentPos;
            } else {
                lastPos = default;
            }
        }
    }
}
