using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace TangerineWin;

/// <summary>
/// The radial overlay shown while a file is being dragged. Each wedge is an OLE drop target;
/// dropping the file on a wedge runs that action. The wedges depend on the dragged file type
/// and on the wheel mode (Shift = convert, Ctrl = tools).
/// </summary>
public sealed class RadialWindow : Window
{
    private const double Size = 380;
    private const double R1 = 64;      // inner radius of the wedges
    private const double R2 = 156;     // outer radius of the wedges
    private const double DiscR = 174;  // radius of the soft background disc
    private const double Inset = 4;    // wedges are drawn inset + a round-joined stroke = rounded corners
    private const double PadAngle = 6.5 / ((R1 + R2) / 2);

    private static readonly Brush DiscFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0xEC, 0xEF, 0xF1)));
    private static readonly Brush NormalFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xF9)));
    private static readonly Brush AccentFill = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x00)));
    private static readonly Brush HubFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush PillBorder = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xDE)));
    private static readonly Brush DarkText = Frozen(new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)));
    private static readonly Brush LightText = Frozen(new SolidColorBrush(Colors.White));

    private sealed class Wedge
    {
        public ActionDef Action = null!;
        public WpfPath Shape = null!;
        public TextBlock Text = null!;
    }

    private readonly Canvas _canvas;
    private readonly List<UIElement> _dynamic = new();
    private readonly List<Wedge> _wedges = new();
    private readonly Border _pill;
    private readonly TextBlock _pillText;
    private readonly ScaleTransform _scale = new(1, 1);

    private WheelMode _mode = WheelMode.Convert;
    private string[] _files = Array.Empty<string>();
    private string? _signature;
    private ActionDef? _hover;
    private int _blocked;   // paths of the current drag refused by PathGuard

    public event Action<ActionDef, string[]>? ActionChosen;

    public RadialWindow()
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

        _canvas = new Canvas
        {
            Width = Size,
            Height = Size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
        };
        Content = _canvas;

        double c = Size / 2;

        // soft background disc with a shadow (not a drop target)
        var disc = new Ellipse
        {
            Width = DiscR * 2,
            Height = DiscR * 2,
            Fill = DiscFill,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.28 },
        };
        Canvas.SetLeft(disc, c - DiscR);
        Canvas.SetTop(disc, c - DiscR);
        _canvas.Children.Add(disc);

        // center hub: the first thing the cursor is over, so it receives the first DragEnter (and the file list)
        double hubR = R1 - 8;
        var hub = new Ellipse
        {
            Width = hubR * 2,
            Height = hubR * 2,
            Fill = HubFill,
            AllowDrop = true,
            Tag = null,
        };
        Canvas.SetLeft(hub, c - hubR);
        Canvas.SetTop(hub, c - hubR);
        AttachDropHandlers(hub);
        _canvas.Children.Add(hub);

        // the white pill in the middle that shows the hovered action
        _pillText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = DarkText,
            FlowDirection = FlowDirection.RightToLeft,
            TextAlignment = TextAlignment.Center,
        };
        _pill = new Border
        {
            Background = Brushes.White,
            BorderBrush = PillBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 5, 12, 5),
            MinWidth = 54,
            IsHitTestVisible = false,
            Child = _pillText,
        };
        _canvas.Children.Add(_pill);
        SetHubText("…");
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    // ---- building the wedges ----------------------------------------------------------------

    private void AddDynamic(UIElement el)
    {
        _canvas.Children.Insert(1 + _dynamic.Count, el); // above the disc, below the hub and the pill
        _dynamic.Add(el);
    }

    private void Rebuild(IReadOnlyList<ActionDef> items)
    {
        foreach (UIElement e in _dynamic) _canvas.Children.Remove(e);
        _dynamic.Clear();
        _wedges.Clear();
        _hover = null;

        int n = items.Count;
        if (n == 0) return;

        double c = Size / 2;
        double step = 2 * Math.PI / Math.Max(n, 4);

        for (int i = 0; i < n; i++)
        {
            // symmetric around the top, clockwise
            double mid = -Math.PI / 2 + (i - (n - 1) / 2.0) * step;
            double a0 = mid - step / 2 + PadAngle;
            double a1 = mid + step / 2 - PadAngle;

            var shape = new WpfPath
            {
                Data = Sector(c, c, R1 + Inset, R2 - Inset, a0, a1),
                Fill = NormalFill,
                Stroke = NormalFill,
                StrokeThickness = Inset * 2,
                StrokeLineJoin = PenLineJoin.Round,
                AllowDrop = true,
                Tag = items[i],
            };
            AttachDropHandlers(shape);
            AddDynamic(shape);

            bool hasIcon = items[i].Icon.Length > 0;
            var tb = new TextBlock
            {
                Foreground = DarkText,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false,
                FlowDirection = FlowDirection.RightToLeft,
                FontFamily = new FontFamily("Segoe UI"),
                Width = 76,
            };
            if (hasIcon)
            {
                tb.Inlines.Add(new Run(items[i].Icon) { FontSize = 20, FontFamily = new FontFamily("Segoe UI Emoji") });
                tb.Inlines.Add(new LineBreak());
                tb.Inlines.Add(new Run(items[i].Label) { FontSize = 11.5, FontWeight = FontWeights.SemiBold });
            }
            else
            {
                tb.Inlines.Add(new Run(items[i].Label) { FontSize = 15, FontWeight = FontWeights.Bold });
            }

            double r = (R1 + R2) / 2;
            tb.Measure(new Size(76, double.PositiveInfinity));
            Canvas.SetLeft(tb, c + Math.Cos(mid) * r - 38);
            Canvas.SetTop(tb, c + Math.Sin(mid) * r - tb.DesiredSize.Height / 2);
            AddDynamic(tb);

            _wedges.Add(new Wedge { Action = items[i], Shape = shape, Text = tb });
        }
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

    private void SetHubText(string text)
    {
        _pillText.Text = text;
        _pill.Measure(new Size(Size, Size));
        double c = Size / 2;
        Canvas.SetLeft(_pill, c - _pill.DesiredSize.Width / 2);
        Canvas.SetTop(_pill, c - _pill.DesiredSize.Height / 2);
    }

    private string IdleText() =>
        _blocked > 0 && _files.Length == 0 ? "ممنوع (خارج C:)"
        : _files.Length == 0 ? "…"
        : _wedges.Count == 0 ? "غير مدعوم"
        : _mode == WheelMode.Convert ? "تحويل" : "أدوات";

    /// <summary>Rebuild the wedges if the mode or the dragged files changed.</summary>
    private void RefreshItems()
    {
        string sig = _mode + "|" + _blocked + "|" + string.Join("|", _files);
        if (sig == _signature) return;
        _signature = sig;

        Rebuild(ActionCatalog.For(_mode, _files));
        SetHubText(IdleText());
    }

    // ---- drag & drop ------------------------------------------------------------------------

    private void AttachDropHandlers(FrameworkElement el)
    {
        el.DragEnter += OnDragEnter;
        el.DragOver += OnDragOver;
        el.DragLeave += OnDragLeave;
        el.Drop += OnDrop;
    }

    /// <summary>Reads the dragged paths. If any of them is outside the allowed drives, the whole drag is refused.</summary>
    private string[] ReadFiles(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] arr)
            {
                string[] ok = PathGuard.FilterAll(arr, out int blocked);
                _blocked = blocked;
                return ok;
            }
        }
        catch { /* ignore */ }
        return Array.Empty<string>();
    }

    private bool IsSupported(ActionDef? a) => a != null && _files.Any(a.Supports);

    // Always answer "Copy" so the source (Explorer) never treats the drop as a Move and deletes the original.
    private DragDropEffects EffectFor(ActionDef? a) =>
        IsSupported(a) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        string[] files = ReadFiles(e);
        if (files.Length > 0) _files = files;
        RefreshItems();

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
            bool on = ReferenceEquals(w.Action, a) && IsSupported(w.Action);
            w.Shape.Fill = on ? AccentFill : NormalFill;
            w.Shape.Stroke = on ? AccentFill : NormalFill;
            w.Text.Foreground = on ? LightText : DarkText;
        }
        SetHubText(a?.Label ?? IdleText());
    }

    // ---- show / hide ------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    /// <summary>Show the wheel centered on the given physical screen pixel, or just switch its mode if already open.</summary>
    public void ShowOrSwitch(int px, int py, WheelMode mode)
    {
        if (IsVisible)
        {
            if (mode == _mode) return;
            _mode = mode;
            RefreshItems();
            return;
        }

        _mode = mode;
        _files = Array.Empty<string>();
        _blocked = 0;
        _signature = null;
        Rebuild(Array.Empty<ActionDef>());
        SetHubText("…");

        Left = -10000;
        Top = -10000;
        Show();

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
