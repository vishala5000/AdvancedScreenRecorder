using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Windows.Media;

namespace ScreenRecorderApp {
    public partial class DrawingOverlay : Window {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT lpPoint);
        
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT {
            public int X;
            public int Y;
        }

        private bool isDrawing = false;
        private DispatcherTimer posTimer;
        private Polyline currentStroke; // Holds the line currently being drawn

        public DrawingOverlay() {
            InitializeComponent();
            this.Loaded += DrawingOverlay_Loaded;
        }

        private void DrawingOverlay_Loaded(object sender, RoutedEventArgs e) {
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
        }

        public void StartDrawing() {
            isDrawing = true;
            currentStroke = null; // Reset so the next click starts a fresh line
            ClickOverlay.IsSuppressed = true; // Prevent click ripples while drawing
            
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
            
            // THIS CLEARS EVERYTHING WHEN YOU PRESS ESC
            DrawCanvas.Children.Clear();
            currentStroke = null;
            this.Hide();
        }

        private void PosTimer_Tick(object sender, EventArgs e) {
            if (!isDrawing) return;

            try {
                bool isLeftClickHeld = (GetAsyncKeyState(0x01) & 0x8000) != 0;

                if (isLeftClickHeld) {
                    GetCursorPos(out POINT cursorPos);
                    Point screenPos = new Point(cursorPos.X, cursorPos.Y);
                    Point windowPos = this.PointFromScreen(screenPos);

                    if (currentStroke == null) {
                        // MOUSE WAS JUST CLICKED: Start a brand new, separate stroke
                        currentStroke = new Polyline {
                            Stroke = Brushes.Red,
                            StrokeThickness = 4,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round
                        };
                        DrawCanvas.Children.Add(currentStroke);
                        currentStroke.Points.Add(windowPos);
                    } else {
                        // MOUSE IS STILL HELD: Continue the current stroke
                        Point lastPoint = currentStroke.Points[currentStroke.Points.Count - 1];
                        double dx = Math.Abs(lastPoint.X - windowPos.X);
                        double dy = Math.Abs(lastPoint.Y - windowPos.Y);
                        
                        // Only add point if moved more than 2 pixels (saves CPU and makes lines smooth)
                        if (dx > 2 || dy > 2) {
                            currentStroke.Points.Add(windowPos);
                        }
                    }
                } else {
                    // MOUSE WAS RELEASED: Finalize the current stroke. 
                    // Setting to null ensures the next click starts a NEW separate line.
                    currentStroke = null;
                }
            } catch { }
        }
    }
}
