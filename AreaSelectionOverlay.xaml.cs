using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace ScreenRecorderApp {
    public partial class AreaSelectionOverlay : Window {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private const int VK_ESCAPE = 0x1B;

        private Point startPoint;
        private bool isSelecting = false;

        public Rect? SelectedArea { get; private set; }

        public AreaSelectionOverlay() {
            InitializeComponent();
            this.MouseDown += AreaSelectionOverlay_MouseDown;
            this.MouseMove += AreaSelectionOverlay_MouseMove;
            this.MouseUp += AreaSelectionOverlay_MouseUp;
            this.KeyDown += AreaSelectionOverlay_KeyDown;
        }

        private void AreaSelectionOverlay_MouseDown(object sender, MouseButtonEventArgs e) {
            if (e.LeftButton == MouseButtonState.Pressed) {
                // CRITICAL FIX: Convert to absolute screen coordinates
                startPoint = this.PointToScreen(e.GetPosition(this));
                isSelecting = true;
                
                Canvas.SetLeft(SelectionRect, startPoint.X);
                Canvas.SetTop(SelectionRect, startPoint.Y);
                SelectionRect.Width = 0;
                SelectionRect.Height = 0;
            }
        }

        private void AreaSelectionOverlay_MouseMove(object sender, MouseEventArgs e) {
            if (isSelecting && e.LeftButton == MouseButtonState.Pressed) {
                // CRITICAL FIX: Convert to absolute screen coordinates
                Point currentPoint = this.PointToScreen(e.GetPosition(this));
                
                double x = Math.Min(startPoint.X, currentPoint.X);
                double y = Math.Min(startPoint.Y, currentPoint.Y);
                double width = Math.Abs(currentPoint.X - startPoint.X);
                double height = Math.Abs(currentPoint.Y - startPoint.Y);
                
                Canvas.SetLeft(SelectionRect, x);
                Canvas.SetTop(SelectionRect, y);
                SelectionRect.Width = width;
                SelectionRect.Height = height;
                
                InfoText.Text = $"Selected: {(int)width} x {(int)height} pixels";
            }
            
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) {
                CancelSelection();
            }
        }

        private void AreaSelectionOverlay_MouseUp(object sender, MouseButtonEventArgs e) {
            if (isSelecting) {
                isSelecting = false;
                
                // CRITICAL FIX: Convert to absolute screen coordinates
                Point endPoint = this.PointToScreen(e.GetPosition(this));
                double x = Math.Min(startPoint.X, endPoint.X);
                double y = Math.Min(startPoint.Y, endPoint.Y);
                double width = Math.Abs(endPoint.X - startPoint.X);
                double height = Math.Abs(endPoint.Y - startPoint.Y);
                
                if (width > 50 && height > 50) {
                    SelectedArea = new Rect(x, y, width, height);
                    this.DialogResult = true;
                    this.Close();
                } else {
                    MessageBox.Show("Selection too small. Please select a larger area.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void AreaSelectionOverlay_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                CancelSelection();
            }
        }

        private void CancelSelection() {
            SelectedArea = null;
            this.DialogResult = false;
            this.Close();
        }
    }
}
