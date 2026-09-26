using System.Windows;
using System.Windows.Media;

namespace FlashLab;

/// <summary>
/// 60,000 stroked ellipses: cheap to record on the UI thread, but the render thread needs close to a second
/// for the first frame. Shows what happens when ContentRendered fires long before the frame is presented.
/// </summary>
public sealed class HeavyVisual : FrameworkElement
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        var random = new Random(1);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), 0.7);
        pen.Freeze();
        for (var i = 0; i < 60000; i++)
        {
            var centre = new Point(random.NextDouble() * ActualWidth, random.NextDouble() * ActualHeight);
            drawingContext.DrawEllipse(null, pen, centre, 2 + random.NextDouble() * 4, 2 + random.NextDouble() * 4);
        }
    }
}
