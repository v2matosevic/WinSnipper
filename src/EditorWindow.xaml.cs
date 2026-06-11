using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace WinSnipper;

/// <summary>
/// Annotation editor: pen / rectangle / ellipse / arrow / crop over the snip.
/// The Surface grid is kept at a 1 DIP == 1 px coordinate space, so a
/// 96-DPI RenderTargetBitmap of it is a pixel-exact composite.
/// </summary>
public partial class EditorWindow : Window
{
    private enum Tool { Pen, Rect, Ellipse, Arrow, Crop }

    private static readonly (string Name, Color Color)[] Palette =
    {
        ("Red", Color.FromRgb(0xE5, 0x39, 0x35)),
        ("Orange", Color.FromRgb(0xFB, 0x8C, 0x00)),
        ("Yellow", Color.FromRgb(0xFD, 0xD8, 0x35)),
        ("Green", Color.FromRgb(0x43, 0xA0, 0x47)),
        ("Blue", Color.FromRgb(0x1E, 0x88, 0xE5)),
        ("Black", Color.FromRgb(0x16, 0x16, 0x16)),
        ("White", Color.FromRgb(0xFA, 0xFA, 0xFA)),
    };

    private string _path;
    private BitmapSource _img = null!;
    private Tool _tool = Tool.Rect;
    private Color _color = Palette[0].Color;
    private readonly List<Border> _swatches = new();
    private ToggleButton[] _toolButtons = null!;

    private bool _drawing;
    private Point _start;
    private Shape? _active;

    private readonly Stack<UIElement> _undo = new();
    private readonly Stack<UIElement> _redo = new();
    private bool _dirty;
    private Rect? _pendingCrop;

    public event Action<BitmapSource>? ImageSaved;

    public EditorWindow(string path, BitmapSource image)
    {
        InitializeComponent();
        _path = path;
        _toolButtons = new[] { BtnRect, BtnPen, BtnEllipse, BtnArrow, BtnCrop };

        SetImage(image);
        BuildSwatches();
        UpdateTitle();
        UpdateUndoButtons();

        ThicknessSlider.ValueChanged += (_, e) => ThicknessLabel.Text = ((int)e.NewValue).ToString();

        // Compact window: wide enough that the full toolbar always fits
        // (MinWidth), tall enough for the snip, capped to 85% of the work area.
        var wa = SystemParameters.WorkArea;
        Width = Math.Clamp(image.PixelWidth + 64, MinWidth, wa.Width * 0.85);
        Height = Math.Clamp(image.PixelHeight + 220, MinHeight, wa.Height * 0.85);

        // Once layout settles, fit the snip to the viewport (never above 1:1 pixels).
        Loaded += (_, _) => Dispatcher.BeginInvoke(FitToViewport,
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void FitToViewport()
    {
        double native = 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        double vw = Scroller.ViewportWidth - 56;
        double vh = Scroller.ViewportHeight - 56;
        double zoom = native;
        if (vw > 0 && vh > 0 && Surface.Width > 0 && Surface.Height > 0)
            zoom = Math.Min(native, Math.Min(vw / Surface.Width, vh / Surface.Height));
        SetZoom(zoom);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int round = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------- image / layout ----------

    private void SetImage(BitmapSource image)
    {
        _img = image;
        BaseImage.Source = image;
        Surface.Width = image.PixelWidth;
        Surface.Height = image.PixelHeight;
        StatusSize.Text = $"{image.PixelWidth} × {image.PixelHeight} px";
    }

    private void BuildSwatches()
    {
        foreach (var (name, color) in Palette)
        {
            var b = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(3, 0, 3, 0),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = name,
                Tag = color,
                VerticalAlignment = VerticalAlignment.Center,
            };
            b.MouseLeftButtonDown += (s, _) => SelectColor((Border)s!);
            _swatches.Add(b);
            ColorsPanel.Children.Add(b);
        }
        SelectColor(_swatches[0]);
    }

    private void SelectColor(Border swatch)
    {
        foreach (var s in _swatches)
            s.BorderBrush = Brushes.Transparent;
        swatch.BorderBrush = Brushes.White;
        _color = (Color)swatch.Tag;
    }

    private void UpdateTitle()
    {
        string name = $"{IOPath.GetFileName(_path)}{(_dirty ? " •" : "")}";
        Title = $"{name} — WinSnipper";
        TitleText.Text = name;
    }

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.15, 6.0);
        ZoomTf.ScaleX = ZoomTf.ScaleY = zoom;
        StatusZoom.Text = $"{zoom * 100:0}%";
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetZoom(ZoomTf.ScaleX * (e.Delta > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    // ---------- tools ----------

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        foreach (var b in _toolButtons)
            b.IsChecked = ReferenceEquals(b, clicked);
        var newTool = Enum.Parse<Tool>((string)clicked.Tag);
        if (_tool == Tool.Crop && newTool != Tool.Crop)
            CancelCrop();
        _tool = newTool;
    }

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));

    private void Ink_Down(object sender, MouseButtonEventArgs e)
    {
        var p = Clamp(e.GetPosition(Ink));
        _drawing = true;
        _start = p;
        Ink.CaptureMouse();

        if (_tool == Tool.Crop)
        {
            CropLayer.Visibility = Visibility.Visible;
            CropActions.Visibility = Visibility.Collapsed;
            CropSel.Visibility = Visibility.Visible;
            _pendingCrop = null;
            UpdateCropVisuals(new Rect(p, p));
            return;
        }

        var brush = new SolidColorBrush(_color);
        double th = ThicknessSlider.Value;
        switch (_tool)
        {
            case Tool.Pen:
                var line = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = th,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                line.Points.Add(p);
                _active = line;
                break;
            case Tool.Rect:
                _active = new Rectangle { Stroke = brush, StrokeThickness = th, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(_active, p.X);
                Canvas.SetTop(_active, p.Y);
                break;
            case Tool.Ellipse:
                _active = new Ellipse { Stroke = brush, StrokeThickness = th };
                Canvas.SetLeft(_active, p.X);
                Canvas.SetTop(_active, p.Y);
                break;
            case Tool.Arrow:
                _active = new Path
                {
                    Stroke = brush,
                    Fill = brush,
                    StrokeThickness = Math.Max(1.5, th * 0.75),
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = BuildArrow(p, p, th),
                };
                break;
        }
        if (_active != null)
            Ink.Children.Add(_active);
    }

    private void Ink_Move(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;
        var p = Clamp(e.GetPosition(Ink));

        if (_tool == Tool.Crop)
        {
            UpdateCropVisuals(NormRect(_start, p));
            return;
        }
        if (_active == null) return;

        switch (_active)
        {
            case Polyline pl:
                pl.Points.Add(p);
                break;
            case Rectangle or Ellipse:
                var r = NormRect(_start, p);
                Canvas.SetLeft(_active, r.X);
                Canvas.SetTop(_active, r.Y);
                _active.Width = r.Width;
                _active.Height = r.Height;
                break;
            case Path path:
                path.Data = BuildArrow(_start, p, ThicknessSlider.Value);
                break;
        }
    }

    private void Ink_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        Ink.ReleaseMouseCapture();
        var p = Clamp(e.GetPosition(Ink));

        if (_tool == Tool.Crop)
        {
            var r = NormRect(_start, p);
            if (r.Width < 3 || r.Height < 3)
            {
                CancelCrop();
            }
            else
            {
                _pendingCrop = r;
                PlaceCropActions(r);
                CropActions.Visibility = Visibility.Visible;
            }
            return;
        }

        if (_active == null) return;
        bool degenerate = _active switch
        {
            Polyline pl => pl.Points.Count < 2,
            Rectangle or Ellipse => _active.Width < 2 && _active.Height < 2,
            _ => (p - _start).Length < 2,
        };
        if (degenerate)
        {
            Ink.Children.Remove(_active);
        }
        else
        {
            _undo.Push(_active);
            _redo.Clear();
            _dirty = true;
            UpdateTitle();
            UpdateUndoButtons();
        }
        _active = null;
    }

    private static Rect NormRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private static Geometry BuildArrow(Point from, Point to, double thickness)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double headLen = Math.Max(10, thickness * 3.5);
            Vector dir = to - from;
            if (dir.Length < 1) dir = new Vector(1, 0);
            else dir.Normalize();
            var perp = new Vector(-dir.Y, dir.X);
            Point basePt = to - dir * headLen;

            ctx.BeginFigure(from, false, false);
            ctx.LineTo(basePt, true, true);

            ctx.BeginFigure(to, true, true);
            ctx.LineTo(basePt + perp * headLen * 0.45, true, true);
            ctx.LineTo(basePt - perp * headLen * 0.45, true, true);
        }
        geo.Freeze();
        return geo;
    }

    // ---------- crop ----------

    private void UpdateCropVisuals(Rect r)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Surface.Width, Surface.Height));
        CropDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r));
        Canvas.SetLeft(CropSel, r.X);
        Canvas.SetTop(CropSel, r.Y);
        CropSel.Width = r.Width;
        CropSel.Height = r.Height;
    }

    private void PlaceCropActions(Rect r)
    {
        CropActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = CropActions.DesiredSize.Width;
        double h = CropActions.DesiredSize.Height;
        double x = Math.Clamp(r.Right - w, 4, Math.Max(4, Surface.Width - w - 4));
        double y = r.Bottom + 8;
        if (y + h > Surface.Height - 4) y = Math.Max(4, r.Bottom - h - 8);
        Canvas.SetLeft(CropActions, x);
        Canvas.SetTop(CropActions, y);
    }

    private void CropApply_Click(object sender, RoutedEventArgs e) => ApplyCrop();

    private void CropCancel_Click(object sender, RoutedEventArgs e) => CancelCrop();

    private void CancelCrop()
    {
        _pendingCrop = null;
        CropLayer.Visibility = Visibility.Collapsed;
        CropSel.Visibility = Visibility.Collapsed;
        CropActions.Visibility = Visibility.Collapsed;
    }

    private void ApplyCrop()
    {
        if (_pendingCrop is not { } r) return;
        r = Rect.Intersect(r, new Rect(0, 0, Surface.Width, Surface.Height));
        CancelCrop();
        if (r.Width < 3 || r.Height < 3) return;

        var composite = Composite();
        var ir = new Int32Rect(
            (int)Math.Round(r.X), (int)Math.Round(r.Y),
            (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        ir.Width = Math.Min(ir.Width, composite.PixelWidth - ir.X);
        ir.Height = Math.Min(ir.Height, composite.PixelHeight - ir.Y);

        var cropped = new CroppedBitmap(composite, ir);
        cropped.Freeze();

        // annotations are baked into the new base image
        Ink.Children.Clear();
        _undo.Clear();
        _redo.Clear();
        UpdateUndoButtons();

        SetImage(cropped);
        _dirty = true;
        UpdateTitle();
    }

    // ---------- undo / output ----------

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var el = _undo.Pop();
        Ink.Children.Remove(el);
        _redo.Push(el);
        _dirty = true;
        UpdateTitle();
        UpdateUndoButtons();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var el = _redo.Pop();
        Ink.Children.Add(el);
        _undo.Push(el);
        _dirty = true;
        UpdateTitle();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = _undo.Count > 0;
        BtnRedo.IsEnabled = _redo.Count > 0;
    }

    private BitmapSource Composite()
    {
        var cropVis = CropLayer.Visibility;
        CropLayer.Visibility = Visibility.Collapsed;
        Surface.UpdateLayout();

        var rtb = new RenderTargetBitmap(_img.PixelWidth, _img.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Surface);
        rtb.Freeze();

        CropLayer.Visibility = cropVis;
        return rtb;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Util.TrySetClipboard(Composite());

    private void CopyClose_Click(object sender, RoutedEventArgs e) => CopyAndClose();

    private void CopyAndClose()
    {
        Util.TrySetClipboard(Composite());
        Close(); // OnClosing auto-saves if dirty
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private BitmapSource Save()
    {
        var img = Composite();
        Util.SavePng(img, _path);
        _dirty = false;
        UpdateTitle();
        ImageSaved?.Invoke(img);
        return img;
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = IOPath.GetFileName(_path),
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
        };
        if (dlg.ShowDialog() != true) return;
        _path = dlg.FileName;
        Save();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape when _tool == Tool.Crop && CropLayer.Visibility == Visibility.Visible:
                CancelCrop();
                e.Handled = true;
                break;
            case Key.Enter when ctrl:
                CopyAndClose();
                e.Handled = true;
                break;
            case Key.Enter when _pendingCrop != null:
                ApplyCrop();
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                Undo_Click(this, null!);
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                Redo_Click(this, null!);
                e.Handled = true;
                break;
            case Key.S when ctrl:
                Save();
                e.Handled = true;
                break;
            case Key.C when ctrl && !(e.OriginalSource is System.Windows.Controls.TextBox):
                Util.TrySetClipboard(Composite());
                e.Handled = true;
                break;
        }
    }

    // No confirmation dialogs — closing silently saves the snip file AND refreshes
    // the clipboard, so what you paste is always the edited image.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_dirty)
            Util.TrySetClipboard(Save());
    }
}
