using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Distraint
{
    /// <summary>Stops the white flash when a window opens: paints its surface in the theme colour before the first frame.</summary>
    internal static class WindowPaint
    {
        public static void Prep(Window w)
        {
            w.SourceInitialized += (s, e) =>
            {
                var src = PresentationSource.FromVisual(w) as HwndSource;
                if (src?.CompositionTarget != null && w.TryFindResource("BgBrush") is SolidColorBrush b)
                    src.CompositionTarget.BackgroundColor = b.Color;
            };
        }
    }
}