using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Distraint.Core;

namespace Distraint
{
    /// <summary>
    /// Full-screen lock cover for ONE monitor (Blocker opens one per screen). Port of overlay_block.xml.
    /// Built in code. Never takes focus, so the app underneath stays "in front" until Blocker pushes it away.
    /// </summary>
    public sealed class OverlayWindow : Window
    {
        private readonly System.Drawing.Rectangle bounds;   // physical pixels

        public OverlayWindow(System.Drawing.Rectangle screenBounds, ImageSource icon, string appLine, string title,
                             string message, int progress, string progressText, Action onClick)
        {
            bounds = screenBounds;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0D, 0x12, 0x20));
            Cursor = Cursors.Hand;

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 720, Margin = new Thickness(32) };
            if (icon != null) stack.Children.Add(new Image { Source = icon, Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(Text(appLine, 15, 0x99, FontWeights.Normal, new Thickness(0, 16, 0, 0)));
            stack.Children.Add(Text(title, 40, 0xFF, FontWeights.Bold, new Thickness(0, 8, 0, 0)));
            stack.Children.Add(Text(message, 18, 0xE6, FontWeights.Normal, new Thickness(0, 12, 0, 0)));

            if (progress >= 0)
            {
                var box = new StackPanel { Width = 300, Margin = new Thickness(0, 28, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                box.Children.Add(new ProgressBar
                {
                    Style = null,                       // ignore the app theme: this cover is always dark
                    Maximum = 1000, Value = progress, Height = 8,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x49, 0xB7, 0xF5)),
                    Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(0)
                });
                box.Children.Add(Text(progressText, 13, 0xB3, FontWeights.Normal, new Thickness(0, 8, 0, 0)));
                stack.Children.Add(box);
            }
            stack.Children.Add(Text("Click anywhere to dismiss", 12, 0x66, FontWeights.Normal, new Thickness(0, 40, 0, 0)));
            Content = stack;

            MouseLeftButtonDown += (s, e) => onClick();
            // Place the window in physical pixels so it covers the monitor exactly, whatever its DPI scaling.
            Loaded += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                Native.SetWindowPos(h, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            };
        }

        private static TextBlock Text(string s, double size, byte alpha, FontWeight weight, Thickness margin) =>
            new TextBlock
            {
                Text = s, FontSize = size, FontWeight = weight, Margin = margin,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF))
            };
    }
}
