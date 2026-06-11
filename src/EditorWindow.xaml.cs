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
    private enum Tool { Pen, Rect, Ellipse, Arrow, Text, Badge, Pixelate, Crop }

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
        _toolButtons = new[] { BtnRect, BtnPen, BtnEllipse, BtnArrow, BtnText, BtnBadge, BtnPixelate, BtnCrop };

        SetImage(image);
        BuildSwatches();
        UpdateTitle();
        UpdateUndoButtons();
        if (!Util.OcrSupported)
            BtnOcr.Visibility = Visibility.Collapsed;

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
        CommitText();
        _tool = newTool;
    }

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));

    private void Ink_Down(object sender, MouseButtonEventArgs e)
    {
        var p = Clamp(e.GetPosition(Ink));

        // Click-to-place tools: no drag phase.
        if (_tool == Tool.Text)
        {
            CommitText();
            StartText(p);
            return;
        }
        if (_tool == Tool.Badge)
        {
            PlaceBadge(p);
            return;
        }

        _drawing = true;
        _start = p;
        Ink.CaptureMouse();

        if (_tool == Tool.Pixelate)
        {
            _pixelatePreview = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            Canvas.SetLeft(_pixelatePreview, p.X);
            Canvas.SetTop(_pixelatePreview, p.Y);
            Ink.Children.Add(_pixelatePreview);
            return;
        }

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
        if (_tool == Tool.Pixelate && _pixelatePreview != null)
        {
            var pr = NormRect(_start, p);
            Canvas.SetLeft(_pixelatePreview, pr.X);
            Canvas.SetTop(_pixelatePreview, pr.Y);
            _pixelatePreview.Width = pr.Width;
            _pixelatePreview.Height = pr.Height;
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

        if (_tool == Tool.Pixelate)
        {
            if (_pixelatePreview != null)
            {
                Ink.Children.Remove(_pixelatePreview);
                _pixelatePreview = null;
            }
            var pr = NormRect(_start, p);
            if (pr.Width >= 4 && pr.Height >= 4)
                ApplyPixelate(pr);
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

    // ---------- pixelate (redact) ----------

    private Rectangle? _pixelatePreview;

    /// <summary>
    /// Bakes a pixelated copy of the region (image + annotations under it) and
    /// lays it on the canvas as a normal undo-able element.
    /// </summary>
    private void ApplyPixelate(Rect r)
    {
        var composite = Composite();
        var ir = new Int32Rect(
            (int)Math.Round(r.X), (int)Math.Round(r.Y),
            (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        ir.Width = Math.Min(ir.Width, composite.PixelWidth - ir.X);
        ir.Height = Math.Min(ir.Height, composite.PixelHeight - ir.Y);
        if (ir.Width < 2 || ir.Height < 2) return;

        var crop = new CroppedBitmap(composite, ir);
        int block = Math.Clamp((int)(Math.Min(ir.Width, ir.Height) / 12.0), 8, 32);
        var small = new TransformedBitmap(crop, new ScaleTransform(1.0 / block, 1.0 / block));
        small.Freeze();

        // Tiny bitmap stretched back up with NearestNeighbor = pixelation.
        var img = new Image
        {
            Source = small,
            Width = ir.Width,
            Height = ir.Height,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, ir.X);
        Canvas.SetTop(img, ir.Y);
        Ink.Children.Add(img);
        _undo.Push(img);
        _redo.Clear();
        _dirty = true;
        UpdateTitle();
        UpdateUndoButtons();
    }

    // ---------- text ----------

    private TextBox? _editBox;

    private double AnnotationFontSize => 10 + ThicknessSlider.Value * 2;

    private void StartText(Point p)
    {
        var box = new TextBox
        {
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
            FontSize = AnnotationFontSize,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 40,
            Padding = new Thickness(2),
        };
        Canvas.SetLeft(box, p.X);
        Canvas.SetTop(box, p.Y);
        box.LostFocus += (_, _) => CommitText();
        Ink.Children.Add(box);
        _editBox = box;
        box.Focus();
    }

    private void CommitText()
    {
        if (_editBox is not { } box) return;
        _editBox = null;
        Ink.Children.Remove(box);

        string text = box.Text.Trim();
        if (text.Length == 0) return;

        var tb = new TextBlock
        {
            Text = text,
            Foreground = box.Foreground,
            FontSize = box.FontSize,
            FontWeight = FontWeights.SemiBold,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 3,
                ShadowDepth = 1,
                Opacity = 0.85,
            },
        };
        Canvas.SetLeft(tb, Canvas.GetLeft(box) + 3);
        Canvas.SetTop(tb, Canvas.GetTop(box) + 3);
        Ink.Children.Add(tb);
        _undo.Push(tb);
        _redo.Clear();
        _dirty = true;
        UpdateTitle();
        UpdateUndoButtons();
    }

    private void CancelText()
    {
        if (_editBox is not { } box) return;
        _editBox = null;
        Ink.Children.Remove(box);
    }

    // ---------- step badges ----------

    private void PlaceBadge(Point p)
    {
        int n = Ink.Children.OfType<Grid>().Count(g => Equals(g.Tag, "badge")) + 1;
        double d = 22 + ThicknessSlider.Value * 2;
        bool lightFill = (_color.R * 0.299 + _color.G * 0.587 + _color.B * 0.114) > 150;

        var badge = new Grid { Width = d, Height = d, Tag = "badge" };
        badge.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(_color),
            Stroke = lightFill ? Brushes.Black : Brushes.White,
            StrokeThickness = 1.5,
        });
        badge.Children.Add(new TextBlock
        {
            Text = n.ToString(),
            Foreground = lightFill ? Brushes.Black : Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = d * 0.52,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(badge, p.X - d / 2);
        Canvas.SetTop(badge, p.Y - d / 2);
        Ink.Children.Add(badge);
        _undo.Push(badge);
        _redo.Clear();
        _dirty = true;
        UpdateTitle();
        UpdateUndoButtons();
    }

    // ---------- OCR ----------

    // Copy Text = "I want the text, I'm done here": copy, save, close — same
    // philosophy as Copy & Close. On failure the editor stays open with the
    // reason in the status bar.
    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        CommitText();
        BtnOcr.IsEnabled = false;
        StatusSize.Text = "Recognizing text…";
        try
        {
            string? text = await Util.OcrAsync(Composite());
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusSize.Text = text is null
                    ? "OCR unavailable — no OCR language installed"
                    : "No text found in the image";
                return;
            }
            Util.TrySetClipboardText(text);
            _clipboardHandled = true; // don't clobber the text with the image on close
            Close();
        }
        catch (Exception ex)
        {
            StatusSize.Text = $"OCR failed: {ex.Message}";
        }
        finally
        {
            BtnOcr.IsEnabled = true;
        }
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
        CommitText(); // pending text annotation joins the output
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

        // While typing a text annotation, only Enter/Esc are ours.
        if (_editBox != null && ReferenceEquals(e.OriginalSource, _editBox))
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelText();
                e.Handled = true;
            }
            return;
        }

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

    private bool _clipboardHandled;

    // No confirmation dialogs — closing silently saves the snip file AND refreshes
    // the clipboard, so what you paste is always the edited image. Exception:
    // when an action (Copy Text) already put its own payload on the clipboard.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_dirty) return;
        var img = Save();
        if (!_clipboardHandled)
            Util.TrySetClipboard(img);
    }
}
