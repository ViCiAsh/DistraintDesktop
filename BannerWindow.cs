using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Distraint.Core;

namespace Distraint
{
    /// <summary>
    /// The small popup at the top of the screen ("Time's up", "5 minutes left"). Port of overlay_timeup / overlay_warning.
    /// Click-through and never focused, so the app underneath stays usable.
    /// </summary>
    public sealed class BannerWindow : Window
    {
        private readonly TextBlock sub;

        public BannerWindow(string title, string subtitle, Color subColor)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;

            sub = new TextBlock { Text = subtitle, FontSize = 15, Foreground = new SolidColorBrush(subColor),
                                  HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.Bold,
                                               Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(sub);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x12, 0x1A, 0x2E)),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(28, 14, 28, 14),
                Child = panel,
                Margin = new Thickness(8)
            };

            SourceInitialized += (s, e) => Native.MakeClickThrough(new WindowInteropHelper(this).Handle);
            Loaded += (s, e) =>
            {
                Rect wa = SystemParameters.WorkArea;     // primary monitor, in WPF units
                Left = wa.Left + (wa.Width - ActualWidth) / 2;
                Top = wa.Top + 24;
            };
        }

        public void SetSubtitle(string text) => sub.Text = text;
    }
}
