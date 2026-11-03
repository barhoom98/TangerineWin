using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace TangerineWin;

/// <summary>
/// The radial overlay shown while a file is dragged with Shift held.
/// Each wedge is an OLE drop target; dropping the file on a wedge runs that action.
/// </summary>
public sealed class RadialWindow : Window
{
    private const double Size = 360;
    private const double R1 = 66;   // inner radius
    private const double R2 = 164;  // outer radius
    private const double GapRad = 0.035;

    private static readonly Brush NormalFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE8, 0x23, 0x23, 0x28)));
    private static readonly Brush AccentFill = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F)));
    private static readonly Brush HubFill = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x11, 0x11, 0x14)));
    private static readonly Brush EdgeStroke = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Colors.White));

    private sealed class Wedge
    {
        public ActionDef Action = null!;
        public WpfPath Shape = null!;
        public TextBlock Text = null!;
    }

    private readonly List<Wedge> _wedges = new();
    private readonly Run _hubLabel = new("اسحب هنا");
    private readonly ScaleTransform _scale = new(1, 1);
    private string[] _files = Array.Empty<string>();
    private ActionDef? _hover;

    public event Action<ActionDef, string[]>? ActionChosen;

    public RadialWindow(IReadOnlyList<ActionDef> actions)
    {
        Width = Size;
        Height = Size;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var canvas = new Canvas
        {
            Width = Size,
            Height = Size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
        };
        Content = canvas;

        double cx = Size / 2, cy = Size / 2;
        int n = actions.Count;
        double step = 2 * Math.PI / n;

        for (int i = 0; i < n; i++)
        {
            // wedge i is centered at angle (-90° + i*step), clockwise from the top
            double mid = -Math.PI / 2 + i * step;
            double a0 = mid - step / 2 + GapRad;
            double a1 = mid + step / 2 - GapRad;

            var shape = new WpfPath
            {
                Data = Sector(cx, cy, R1, R2, a0, a1),
                Fill = NormalFill,
                Stroke = EdgeStroke,
                StrokeThickness = 1,
                AllowDrop = true,
                Tag = actions[i],
            };
            AttachDropHandlers(shape);
            canvas.Children.Add(shape);

            var tb = new TextBlock
            {
                Foreground = TextBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false,
                FlowDirection = FlowDirection.RightToLeft,
                Width = 78,
            };
            tb.Inlines.Add(new Run(actions[i].Icon) { FontSize = 22, FontFamily = new FontFamily("Segoe UI Emoji") });
            tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(new Run(actions[i].Label) { FontSize = 11, FontFamily = new FontFamily("Segoe UI") });
            PlaceCentered(canvas, tb, cx + Math.Cos(mid) * (R1 + R2) / 2, cy + Math.Sin(mid) * (R1 + R2) / 2, 78);

            _wedges.Add(new Wedge { Action = actions[i], Shape = shape, Text = tb });
        }

        // center hub: also a drop target so we can read the dragged files as soon as the cursor is over it
        double hubR = R1 - 8;
        var hub = new System.Windows.Shapes.Ellipse
        {
            Width = hubR * 2,
            Height = hubR * 2,
            Fill = HubFill,
            Stroke = EdgeStroke,
            StrokeThickness = 1,
            AllowDrop = true,
            Tag = null,
        };
        Canvas.SetLeft(hub, cx - hubR);
        Canvas.SetTop(hub, cy - hubR);
        AttachDropHandlers(hub);
        canvas.Children.Add(hub);

        var hubText = new TextBlock
        {
            Foreground = TextBrush,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
            FlowDirection = FlowDirection.RightToLeft,
        };
        hubText.Inlines.Add(new Run("🍊") { FontSize = 26, FontFamily = new FontFamily("Segoe UI Emoji") });
        hubText.Inlines.Add(new LineBreak());
        _hubLabel.FontSize = 11;
        _hubLabel.FontFamily = new FontFamily("Segoe UI");
        hubText.Inlines.Add(_hubLabel);
        PlaceCentered(canvas, hubText, cx, cy, hubR * 2 - 8);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static void PlaceCentered(Canvas canvas, TextBlock tb, double x, double y, double width)
    {
        tb.Width = width;
        tb.Measure(new Size(width, double.PositiveInfinity));
        Canvas.SetLeft(tb, x - width / 2);
        Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
        canvas.Children.Add(tb);
    }

    private static Geometry Sector(double cx, double cy, double r1, double r2, double a0, double a1)
    {
        Point P(double r, double a) => new(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        var fig = new PathFigure { StartPoint = P(r2, a0), IsClosed = true };
        fig.Segments.Add(new ArcSegment(P(r2, a1), new Size(r2, r2), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(r1, a1), true));
        fig.Segments.Add(new ArcSegment(P(r1, a0), new Size(r1, r1), 0, false, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    // ---- drag & drop ------------------------------------------------------------------------

    private void AttachDropHandlers(FrameworkElement el)
    {
        el.DragEnter += OnDragEnter;
        el.DragOver += OnDragOver;
        el.DragLeave += OnDragLeave;
        el.Drop += OnDrop;
    }

    private static string[] ReadFiles(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] arr)
                return arr;
        }
        catch { /* ignore */ }
        return Array.Empty<string>();
    }

    private bool IsSupported(ActionDef? a) =>
        a != null && (_files.Length == 0 || _files.Any(a.Supports));

    // Always answer "Copy" so the source (Explorer) never treats the drop as a Move and deletes the original
    // (Shift+drag normally means Move).
    private DragDropEffects EffectFor(ActionDef? a) =>
        IsSupported(a) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _files = ReadFiles(e);
        var a = (sender as FrameworkElement)?.Tag as ActionDef;
        SetHover(a);
        e.Effects = EffectFor(a);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var a = (sender as FrameworkElement)?.Tag as ActionDef;
        e.Effects = EffectFor(a);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        SetHover(null);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var a = (sender as FrameworkElement)?.Tag as ActionDef;
        string[] files = ReadFiles(e);
        if (files.Length > 0) _files = files;

        e.Effects = EffectFor(a);
        e.Handled = true;

        if (a != null && _files.Any(a.Supports))
        {
            string[] chosen = _files;
            HideRing();
            ActionChosen?.Invoke(a, chosen);
        }
    }

    private void SetHover(ActionDef? a)
    {
        _hover = a;
        foreach (Wedge w in _wedges)
        {
            bool supported = IsSupported(w.Action) || _files.Length == 0;
            bool hovered = ReferenceEquals(w.Action, a);
            w.Shape.Fill = hovered && supported ? AccentFill : NormalFill;
            w.Text.Opacity = supported ? 1.0 : 0.3;
        }
        _hubLabel.Text = a == null ? "اسحب هنا" : a.Label;
    }

    // ---- show / hide ------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    /// <summary>Show the ring centered on the given physical screen pixel.</summary>
    public void ShowAt(int px, int py)
    {
        _files = Array.Empty<string>();
        SetHover(null);

        if (!IsVisible)
        {
            Left = -10000;
            Top = -10000;
            Show();
        }

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int size = (int)Math.Round(Size * dpi);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, px - size / 2, py - size / 2, size, size,
                            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        var anim = new DoubleAnimation(0.82, 1.0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    public void HideRing()
    {
        if (IsVisible) Hide();
    }
}
