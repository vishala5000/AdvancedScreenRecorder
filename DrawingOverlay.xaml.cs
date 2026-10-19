using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenRecorderApp {
    public partial class DrawingOverlay : Window {
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
            posTimer.Start();
        }

        public void StopDrawing() {
            isDrawing = false;
            posTimer.Stop();
            DrawCanvas.Children.Clear();
            this.Hide();
        }

        private void PosTimer_Tick(object sender, EventArgs e) {
            if (!isDrawing) return;
            
            // Get global mouse position
            var pos = PointToScreen(Mouse.GetPosition(this));
            Point currentPos = new Point(pos.X, pos.Y);

            if (lastPos != default && lastPos != currentPos) {
                Line line = new Line {
                    X1 = lastPos.X, Y1 = lastPos.Y,
                    X2 = currentPos.X, Y2 = currentPos.Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(230, 0, 0)), // Red
                    StrokeThickness = 4,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                DrawCanvas.Children.Add(line);
            }
            lastPos = currentPos;
        }
    }
}