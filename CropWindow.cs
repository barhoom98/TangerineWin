using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TangerineWin;

/// <summary>
/// A small crop editor: drag the box to move it, drag the handles to resize it, or drag on the
/// image to draw a new one. Pick an aspect ratio or leave it free, then save a "_crop" copy.
/// </summary>
internal sealed class CropWindow : Window
{
    private const double Tol = 12;     // handle hit tolerance (DIPs)
    private const double MinPx = 8;    // minimum selection size (image pixels)

    private enum DragMode { None, New, Move, Resize }

    private readonly string _src;
    private readonly BitmapSource _bmp;
    private readonly double _w;   // image size in pixels
    private readonly double _h;

    private readonly Canvas _canvas;
    private readonly Image _img;
    private readonly Rectangle _dimTop, _dimBottom, _dimLeft, _dimRight, _box;
    private readonly Rectangle[] _handles = new Rectangle[8]; // TL, T, TR, R, BR, B, BL, L
    private readonly Border _infoBox;
    private readonly TextBlock _info;
    private readonly List<ToggleButton> _ratioButtons = new();

    // selection in image pixels
    private double _l, _t, _r, _b;
    private double _aspect;           // 0 = free, otherwise width / height

    // image -> canvas mapping
    private double _scale = 1, _offX, _offY;

    // active drag
    private DragMode _mode = DragMode.None;
    private int _handle;
    private Point _startP;
    private double _sl, _st, _sr, _sb;
    private Point _anchor;

    private bool _done;

    public event Action<ActionResult>? Finished;

    public CropWindow(string src)
    {
        _src = src;
        _bmp = ImageOps.Load(src);
        _w = _bmp.PixelWidth;
        _h = _bmp.PixelHeight;

        Title = "قص الصورة — " + System.IO.Path.GetFileName(src);
        Width = 980;
        Height = 740;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20));
        FlowDirection = FlowDirection.LeftToRight;

        // ---- canvas ----
        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E)),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
        };
        _img = new Image { Source = _bmp, Stretch = Stretch.Fill, IsHitTestVisible = false };
        _canvas.Children.Add(_img);

        Brush dim = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        Rectangle Dim()
        {
            var r = new Rectangle { Fill = dim, IsHitTestVisible = false };
            _canvas.Children.Add(r);
            return r;
        }
        _dimTop = Dim();
        _dimBottom = Dim();
        _dimLeft = Dim();
        _dimRight = Dim();

        _box = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
        _canvas.Children.Add(_box);

        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = 12,
                Height = 12,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                StrokeThickness = 1,
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_handles[i]);
        }

        _info = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI") };
        _infoBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _info,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_infoBox);

        _canvas.SizeChanged += (_, _) => Layout();
        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseUp;

        // ---- toolbar ----
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(12, 10, 12, 10) };

        var save = new Button
        {
            Content = "حفظ",
            Padding = new Thickness(22, 6, 22, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x00)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            IsDefault = true,
        };
        save.Click += (_, _) => Save();
        DockPanel.SetDock(save, Dock.Right);
        bar.Children.Add(save);

        var cancel = new Button { Content = "إلغاء", Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(cancel, Dock.Right);
        bar.Children.Add(cancel);

        var ratios = new StackPanel { Orientation = Orientation.Horizontal };
        ratios.Children.Add(new TextBlock
        {
            Text = "النسبة:",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FlowDirection = FlowDirection.RightToLeft,
        });
        AddRatio(ratios, "حر", 0, true);
        AddRatio(ratios, "1:1", 1.0, false);
        AddRatio(ratios, "4:3", 4.0 / 3, false);
        AddRatio(ratios, "3:4", 3.0 / 4, false);
        AddRatio(ratios, "16:9", 16.0 / 9, false);
        AddRatio(ratios, "9:16", 9.0 / 16, false);
        DockPanel.SetDock(ratios, Dock.Left);
        bar.Children.Add(ratios);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_canvas);
        Content = root;

        ResetSelection();
    }

    private void AddRatio(StackPanel parent, string text, double aspect, bool selected)
    {
        var tb = new ToggleButton
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 6, 0),
            IsChecked = selected,
            MinWidth = 44,
        };
        tb.Click += (_, _) =>
        {
            foreach (ToggleButton other in _ratioButtons) other.IsChecked = ReferenceEquals(other, tb);
            SetAspect(aspect);
        };
        _ratioButtons.Add(tb);
        parent.Children.Add(tb);
    }

    // ---- selection helpers --------------------------------------------------------------

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    private void ResetSelection()
    {
        double mx = _w * 0.1, my = _h * 0.1;
        _l = mx; _t = my; _r = _w - mx; _b = _h - my;
        if (_aspect > 0) Refit();
    }

    /// <summary>Make the selection match the aspect ratio, keeping its center, inside the image.</summary>
    private void Refit()
    {
        double cx = (_l + _r) / 2, cy = (_t + _b) / 2;
        double w = _r - _l, h = _b - _t;
        if (w / Math.Max(h, 1e-6) > _aspect) w = h * _aspect; else h = w / _aspect;
        if (w > _w) { w = _w; h = w / _aspect; }
        if (h > _h) { h = _h; w = h * _aspect; }
        _l = Clamp(cx - w / 2, 0, _w - w);
        _t = Clamp(cy - h / 2, 0, _h - h);
        _r = _l + w;
        _b = _t + h;
    }

    private void SetAspect(double aspect)
    {
        _aspect = aspect;
        if (aspect > 0) Refit();
        Layout();
    }

    private Point ToPx(Point p) =>
        new(Clamp((p.X - _offX) / _scale, 0, _w), Clamp((p.Y - _offY) / _scale, 0, _h));

    private Rect DispRect() =>
        new(_offX + _l * _scale, _offY + _t * _scale, (_r - _l) * _scale, (_b - _t) * _scale);

    private Point HandleCenter(int i, Rect d)
    {
        double x0 = d.Left, x1 = d.Left + d.Width / 2, x2 = d.Right;
        double y0 = d.Top, y1 = d.Top + d.Height / 2, y2 = d.Bottom;
        return i switch
        {
            0 => new Point(x0, y0),
            1 => new Point(x1, y0),
            2 => new Point(x2, y0),
            3 => new Point(x2, y1),
            4 => new Point(x2, y2),
            5 => new Point(x1, y2),
            6 => new Point(x0, y2),
            _ => new Point(x0, y1),
        };
    }

    private static bool IsLeft(int h) => h == 0 || h == 6 || h == 7;
    private static bool IsRight(int h) => h == 2 || h == 3 || h == 4;
    private static bool IsTop(int h) => h == 0 || h == 1 || h == 2;
    private static bool IsBottom(int h) => h == 4 || h == 5 || h == 6;
    private static bool IsEdge(int h) => h % 2 == 1;

    // ---- layout -------------------------------------------------------------------------

    private void Layout()
    {
        double cw = _canvas.ActualWidth, ch = _canvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        _scale = Math.Min(cw / _w, ch / _h);
        double iw = _w * _scale, ih = _h * _scale;
        _offX = (cw - iw) / 2;
        _offY = (ch - ih) / 2;

        Place(_img, _offX, _offY, iw, ih);

        Rect d = DispRect();
        Place(_dimTop, _offX, _offY, iw, d.Top - _offY);
        Place(_dimBottom, _offX, d.Bottom, iw, _offY + ih - d.Bottom);
        Place(_dimLeft, _offX, d.Top, d.Left - _offX, d.Height);
        Place(_dimRight, d.Right, d.Top, _offX + iw - d.Right, d.Height);
        Place(_box, d.Left, d.Top, d.Width, d.Height);

        for (int i = 0; i < 8; i++)
        {
            bool show = !(_aspect > 0 && IsEdge(i)); // with a fixed ratio only the corners resize
            _handles[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Point c = HandleCenter(i, d);
            Canvas.SetLeft(_handles[i], c.X - 6);
            Canvas.SetTop(_handles[i], c.Y - 6);
        }

        _info.Text = $"{Math.Round(_r - _l)} × {Math.Round(_b - _t)}";
        Canvas.SetLeft(_infoBox, d.Left + 6);
        Canvas.SetTop(_infoBox, Math.Max(_offY + 2, d.Top + 6));
    }

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }

    // ---- mouse --------------------------------------------------------------------------

    private (DragMode Mode, int Handle) Hit(Point p)
    {
        Rect d = DispRect();
        for (int i = 0; i < 8; i++)
        {
            if (_aspect > 0 && IsEdge(i)) continue;
            Point c = HandleCenter(i, d);
            if (Math.Abs(p.X - c.X) <= Tol && Math.Abs(p.Y - c.Y) <= Tol) return (DragMode.Resize, i);
        }
        return d.Contains(p) ? (DragMode.Move, -1) : (DragMode.New, -1);
    }

    private static Cursor CursorFor(DragMode mode, int h)
    {
        if (mode == DragMode.Move) return Cursors.SizeAll;
        if (mode == DragMode.New) return Cursors.Cross;
        return h switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            _ => Cursors.SizeWE,
        };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Point p = e.GetPosition(_canvas);
        (_mode, _handle) = Hit(p);
        _startP = p;
        _sl = _l; _st = _t; _sr = _r; _sb = _b;

        if (_mode == DragMode.New)
        {
            Point px = ToPx(p);
            _anchor = px;
            _l = _r = px.X;
            _t = _b = px.Y;
        }
        _canvas.CaptureMouse();
        Layout();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point p = e.GetPosition(_canvas);

        if (_mode == DragMode.None || !_canvas.IsMouseCaptured)
        {
            var (m, h) = Hit(p);
            _canvas.Cursor = CursorFor(m, h);
            return;
        }

        switch (_mode)
        {
            case DragMode.Move:
            {
                double dx = (p.X - _startP.X) / _scale;
                double dy = (p.Y - _startP.Y) / _scale;
                double w = _sr - _sl, h = _sb - _st;
                _l = Clamp(_sl + dx, 0, _w - w);
                _t = Clamp(_st + dy, 0, _h - h);
                _r = _l + w;
                _b = _t + h;
                break;
            }
            case DragMode.New:
                CornerDrag(_anchor.X, _anchor.Y, ToPx(p));
                break;
            case DragMode.Resize:
            {
                Point px = ToPx(p);
                if (_aspect > 0)
                {
                    // fixed ratio: the opposite corner stays put
                    double ax = IsLeft(_handle) ? _sr : _sl;
                    double ay = IsTop(_handle) ? _sb : _st;
                    CornerDrag(ax, ay, px);
                }
                else
                {
                    double l = _sl, t = _st, r = _sr, b = _sb;
                    if (IsLeft(_handle)) l = Math.Min(px.X, r - MinPx);
                    if (IsRight(_handle)) r = Math.Max(px.X, l + MinPx);
                    if (IsTop(_handle)) t = Math.Min(px.Y, b - MinPx);
                    if (IsBottom(_handle)) b = Math.Max(px.Y, t + MinPx);
                    _l = l; _t = t; _r = r; _b = b;
                }
                break;
            }
        }
        Layout();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == DragMode.New && (_r - _l < MinPx || _b - _t < MinPx))
            ResetSelection();

        _mode = DragMode.None;
        _canvas.ReleaseMouseCapture();
        Layout();
    }

    /// <summary>Selection spanning the anchor point and the dragged point, honoring the aspect ratio.</summary>
    private void CornerDrag(double ax, double ay, Point pt)
    {
        double dx = pt.X - ax, dy = pt.Y - ay;
        double sx = dx >= 0 ? 1 : -1, sy = dy >= 0 ? 1 : -1;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        double maxW = sx > 0 ? _w - ax : ax;
        double maxH = sy > 0 ? _h - ay : ay;

        if (_aspect > 0)
        {
            if (w / Math.Max(h, 1e-6) > _aspect) w = h * _aspect; else h = w / _aspect;
            if (w > maxW) { w = maxW; h = w / _aspect; }
            if (h > maxH) { h = maxH; w = h * _aspect; }
        }
        else
        {
            w = Math.Min(w, maxW);
            h = Math.Min(h, maxH);
        }

        double x2 = ax + sx * w, y2 = ay + sy * h;
        _l = Math.Min(ax, x2);
        _r = Math.Max(ax, x2);
        _t = Math.Min(ay, y2);
        _b = Math.Max(ay, y2);
    }

    // ---- save / close -------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Enter) { Save(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Save()
    {
        if (_done) return;
        try
        {
            int x = (int)Clamp(Math.Round(_l), 0, _w - 1);
            int y = (int)Clamp(Math.Round(_t), 0, _h - 1);
            int w = (int)Math.Max(1, Math.Min(Math.Round(_r - _l), _w - x));
            int h = (int)Math.Max(1, Math.Min(Math.Round(_b - _t), _h - y));

            string dst = ImageOps.SaveCropped(_src, _bmp, new Int32Rect(x, y, w, h));
            Finish(new ActionResult($"تم حفظ الصورة المقصوصة ({w} × {h})", dst));
        }
        catch (Exception ex)
        {
            Finish(new ActionResult("خطأ: " + ex.Message, null));
        }
        Close();
    }

    private void Finish(ActionResult result)
    {
        if (_done) return;
        _done = true;
        Finished?.Invoke(result);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Finish(new ActionResult("تم الإلغاء", null));
    }
}
