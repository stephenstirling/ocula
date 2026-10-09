using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Ocula.Models;
using Ocula.Services;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Ocula;

/// <summary>
/// Crop, straighten, rotate, flip and basic adjustments, previewed live with Win2D and saved
/// as a copy by default. "Save over original" sends the original to the Recycle Bin first.
/// </summary>
public sealed partial class EditWindow : Window
{
    private enum Drag { None, Move, N, S, E, W, NE, NW, SE, SW }

    private readonly MainWindow _main;
    private readonly ImageItem _item;
    private readonly EditState _s = new();
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private CanvasBitmap? _preview;
    private int _orientation = 1;
    private bool _cropTab = true;
    private double? _aspect; // width / height in pixels; null = free

    // Where the frame is drawn on screen, for turning pointer positions into crop positions.
    private float _ox, _oy, _fit, _fw, _fh;
    private Drag _drag;
    private Point _dragStart;
    private Rect _dragCrop;
    private bool _saving;

    public EditWindow(MainWindow main, ImageItem item)
    {
        InitializeComponent();
        _main = main;
        _item = item;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Maximize();
        Root.RequestedTheme = main.CurrentTheme;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

        TitleText.Text = $"Edit · {item.Name}";
        OverwriteButton.IsEnabled = EditRenderer.CanOverwrite(item.Path);
        ToolTipService.SetToolTip(OverwriteButton, OverwriteButton.IsEnabled
            ? "Saves the edit in place of the original; the original goes to the Recycle Bin."
            : "Only JPEG, PNG and TIFF files can be overwritten. RAW and other formats are saved as a JPEG copy.");

        // Double-click any slider to put it back to zero.
        foreach (var slider in new[] { ExposureSlider, ContrastSlider, HighlightsSlider, ShadowsSlider, WarmthSlider, SaturationSlider, StraightenSlider })
            slider.DoubleTapped += (s, _) => ((Slider)s).Value = 0;

        Root.KeyDown += Root_KeyDown;
        Closed += (_, _) => { _preview?.Dispose(); Stage.RemoveFromVisualTree(); };
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // A screen-sized copy keeps every slider movement instant.
            try
            {
                var (pixels, w, h, orientation) = await Task.Run(() => HdrRenderer.DecodePreviewAsync(_item.Path, 2048));
                _preview = CanvasBitmap.CreateFromBytes(_device, pixels, w, h, DirectXPixelFormat.R8G8B8A8UIntNormalized);
                _orientation = orientation;
            }
            catch
            {
                // No Windows decoder (some RAW files): use Ocula's own preview, already upright.
                var bytes = await ThumbnailService.GetBytesAsync(_item.Path, 2048, allowDecode: true, highPriority: true, compress: false)
                            ?? throw new InvalidOperationException("This photo couldn't be opened.");
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                stream.Seek(0);
                _preview = await CanvasBitmap.LoadAsync(_device, stream);
                _orientation = 1;
            }
            Busy.IsActive = false;
            UpdateSizeText();
            Stage.Invalidate();
        }
        catch (Exception ex)
        {
            Busy.IsActive = false;
            StatusText.Text = "Couldn't open this photo for editing: " + ex.Message;
        }
    }

    // ================= Drawing =================

    private Size Frame()
    {
        if (_preview == null) return new Size(1, 1);
        return EditRenderer.FrameSize(_preview.SizeInPixels.Width, _preview.SizeInPixels.Height,
                                      EditRenderer.QuarterTurns(_orientation, _s));
    }

    private void Stage_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_preview == null) return;
        var ds = args.DrawingSession;
        var w = (float)_preview.SizeInPixels.Width;
        var h = (float)_preview.SizeInPixels.Height;
        var frame = Frame();
        _fw = (float)frame.Width;
        _fh = (float)frame.Height;

        var after = EditRenderer.Frame(EditRenderer.Adjust(_preview, _s), w, h, _orientation, _s);
        var area = new Rect(24, 24, Math.Max(1, sender.ActualWidth - 48), Math.Max(1, sender.ActualHeight - 72));

        if (_cropTab)
        {
            // The whole frame, with the crop box on top.
            _fit = (float)Math.Min(area.Width / _fw, area.Height / _fh);
            _ox = (float)(area.X + (area.Width - _fw * _fit) / 2);
            _oy = (float)(area.Y + (area.Height - _fh * _fit) / 2);
            ds.Transform = Matrix3x2.CreateScale(_fit) * Matrix3x2.CreateTranslation(_ox, _oy);
            ds.DrawImage(after);
            ds.Transform = Matrix3x2.Identity;
            DrawCropOverlay(ds);
            return;
        }

        // Adjust tab: just the cropped result, fitted to the stage.
        var src = new Rect(_s.Crop.X * _fw, _s.Crop.Y * _fh, _s.Crop.Width * _fw, _s.Crop.Height * _fh);
        var fit = Math.Min(area.Width / src.Width, area.Height / src.Height);
        var dest = new Rect(area.X + (area.Width - src.Width * fit) / 2, area.Y + (area.Height - src.Height * fit) / 2,
                            src.Width * fit, src.Height * fit);

        if (CompareToggle.IsChecked == true)
        {
            var before = EditRenderer.Frame(_preview, w, h, _orientation, _s);
            var middle = dest.X + dest.Width / 2;
            using (ds.CreateLayer(1f, new Rect(dest.X, dest.Y, dest.Width / 2, dest.Height))) ds.DrawImage(before, dest, src);
            using (ds.CreateLayer(1f, new Rect(middle, dest.Y, dest.Width / 2, dest.Height))) ds.DrawImage(after, dest, src);
            ds.DrawLine((float)middle, (float)dest.Y, (float)middle, (float)dest.Bottom, Colors.White, 2);
            ds.DrawText("Before", (float)dest.X + 12, (float)dest.Y + 10, Colors.White);
            ds.DrawText("After", (float)(dest.Right - 60), (float)dest.Y + 10, Colors.White);
        }
        else
        {
            ds.DrawImage(after, dest, src);
        }
    }

    private Rect CropOnScreen() => new(_ox + _s.Crop.X * _fw * _fit, _oy + _s.Crop.Y * _fh * _fit,
                                       _s.Crop.Width * _fw * _fit, _s.Crop.Height * _fh * _fit);

    private void DrawCropOverlay(Microsoft.Graphics.Canvas.CanvasDrawingSession ds)
    {
        var frame = new Rect(_ox, _oy, _fw * _fit, _fh * _fit);
        var c = CropOnScreen();
        var shade = ColorHelper.FromArgb(150, 0, 0, 0);

        // Darken everything outside the crop.
        ds.FillRectangle(new Rect(frame.X, frame.Y, frame.Width, c.Y - frame.Y), shade);
        ds.FillRectangle(new Rect(frame.X, c.Bottom, frame.Width, frame.Bottom - c.Bottom), shade);
        ds.FillRectangle(new Rect(frame.X, c.Y, c.X - frame.X, c.Height), shade);
        ds.FillRectangle(new Rect(c.Right, c.Y, frame.Right - c.Right, c.Height), shade);

        // Rule-of-thirds guides, border and corner handles.
        var guide = ColorHelper.FromArgb(110, 255, 255, 255);
        for (var i = 1; i < 3; i++)
        {
            var x = (float)(c.X + c.Width * i / 3);
            var y = (float)(c.Y + c.Height * i / 3);
            ds.DrawLine(x, (float)c.Y, x, (float)c.Bottom, guide);
            ds.DrawLine((float)c.X, y, (float)c.Right, y, guide);
        }
        ds.DrawRectangle(c, Colors.White, 1.5f);
        const float len = 18, thick = 4;
        foreach (var (x, y, sx, sy) in new[] { (c.X, c.Y, 1, 1), (c.Right, c.Y, -1, 1), (c.X, c.Bottom, 1, -1), (c.Right, c.Bottom, -1, -1) })
        {
            ds.DrawLine((float)x, (float)y, (float)x + sx * len, (float)y, Colors.White, thick);
            ds.DrawLine((float)x, (float)y, (float)x, (float)y + sy * len, Colors.White, thick);
        }
    }

    // ================= Crop handles =================

    private Drag HitTest(Point p)
    {
        var c = CropOnScreen();
        const double grab = 16;
        bool near(double a, double b) => Math.Abs(a - b) <= grab;
        var inX = p.X >= c.X - grab && p.X <= c.Right + grab;
        var inY = p.Y >= c.Y - grab && p.Y <= c.Bottom + grab;
        if (!inX || !inY) return Drag.None;

        var left = near(p.X, c.X); var right = near(p.X, c.Right);
        var top = near(p.Y, c.Y); var bottom = near(p.Y, c.Bottom);
        if (top && left) return Drag.NW;
        if (top && right) return Drag.NE;
        if (bottom && left) return Drag.SW;
        if (bottom && right) return Drag.SE;
        if (top) return Drag.N;
        if (bottom) return Drag.S;
        if (left) return Drag.W;
        if (right) return Drag.E;
        return c.Contains(p) ? Drag.Move : Drag.None;
    }

    private void Stage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropTab || _preview == null) return;
        var p = e.GetCurrentPoint(Stage).Position;
        _drag = HitTest(p);
        if (_drag == Drag.None) return;
        _dragStart = p;
        _dragCrop = _s.Crop;
        Stage.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Stage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        var p = e.GetCurrentPoint(Stage).Position;
        var dx = (p.X - _dragStart.X) / (_fw * _fit);
        var dy = (p.Y - _dragStart.Y) / (_fh * _fit);
        _s.Crop = Resize(_dragCrop, _drag, dx, dy);
        UpdateSizeText();
        Stage.Invalidate();
    }

    private void Stage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _drag = Drag.None;
        Stage.ReleasePointerCaptures();
    }

    /// <summary>Moves or resizes the crop box, keeping it inside the photo and to the chosen shape.</summary>
    private Rect Resize(Rect r, Drag drag, double dx, double dy)
    {
        const double min = 0.04;
        if (drag == Drag.Move)
        {
            var x = Math.Clamp(r.X + dx, 0, 1 - r.Width);
            var y = Math.Clamp(r.Y + dy, 0, 1 - r.Height);
            return new Rect(x, y, r.Width, r.Height);
        }

        double x0 = r.X, y0 = r.Y, x1 = r.Right, y1 = r.Bottom;
        if (drag is Drag.W or Drag.NW or Drag.SW) x0 = Math.Clamp(x0 + dx, 0, x1 - min);
        if (drag is Drag.E or Drag.NE or Drag.SE) x1 = Math.Clamp(x1 + dx, x0 + min, 1);
        if (drag is Drag.N or Drag.NW or Drag.NE) y0 = Math.Clamp(y0 + dy, 0, y1 - min);
        if (drag is Drag.S or Drag.SW or Drag.SE) y1 = Math.Clamp(y1 + dy, y0 + min, 1);
        if (_aspect is not double aspect) return new Rect(x0, y0, x1 - x0, y1 - y0);

        // Keep the chosen shape. In crop units, width/height = aspect × frame height / frame width.
        var shape = aspect * _fh / _fw;
        double width = x1 - x0, height = y1 - y0;
        if (drag is Drag.N or Drag.S) width = height * shape;
        else height = width / shape;

        // Shrink if the box would leave the photo, anchored on the side not being dragged.
        var anchorRight = drag is Drag.W or Drag.NW or Drag.SW;
        var anchorBottom = drag is Drag.N or Drag.NW or Drag.NE;
        var maxW = drag is Drag.N or Drag.S ? Math.Min(r.X + r.Width / 2, 1 - (r.X + r.Width / 2)) * 2
                 : anchorRight ? x1 : 1 - x0;
        var maxH = drag is Drag.E or Drag.W ? Math.Min(r.Y + r.Height / 2, 1 - (r.Y + r.Height / 2)) * 2
                 : anchorBottom ? y1 : 1 - y0;
        if (width > maxW) { width = maxW; height = width / shape; }
        if (height > maxH) { height = maxH; width = height * shape; }

        var nx = drag is Drag.N or Drag.S ? r.X + r.Width / 2 - width / 2 : anchorRight ? x1 - width : x0;
        var ny = drag is Drag.E or Drag.W ? r.Y + r.Height / 2 - height / 2 : anchorBottom ? y1 - height : y0;
        return new Rect(nx, ny, width, height);
    }

    // ================= Tools =================

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _cropTab = (string)((FrameworkElement)sender).Tag == "crop";
        CropTab.IsChecked = _cropTab;
        AdjustTab.IsChecked = !_cropTab;
        CropPanel.Visibility = _cropTab ? Visibility.Visible : Visibility.Collapsed;
        AdjustPanel.Visibility = _cropTab ? Visibility.Collapsed : Visibility.Visible;
        if (_cropTab) CompareToggle.IsChecked = false;
        Stage.Invalidate();
    }

    private void Aspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        _aspect = AspectBox.SelectedIndex switch
        {
            1 => _fw > 0 ? _fw / _fh : null, // original shape
            2 => 1.0, 3 => 4.0 / 5, 4 => 3.0 / 2, 5 => 16.0 / 9, 6 => 9.0 / 16,
            _ => null
        };
        ApplyAspect();
    }

    /// <summary>Centres the largest crop of the chosen shape.</summary>
    private void ApplyAspect()
    {
        if (_preview == null || _aspect is not double aspect) { Stage?.Invalidate(); return; }
        var frame = Frame();
        var shape = aspect * frame.Height / frame.Width;
        double w = 1, h = 1 / shape;
        if (h > 1) { h = 1; w = shape; }
        _s.Crop = new Rect((1 - w) / 2, (1 - h) / 2, w, h);
        UpdateSizeText();
        Stage.Invalidate();
    }

    private void Straighten_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        _s.Straighten = (float)e.NewValue;
        StraightenSlider.Header = $"Straighten {e.NewValue:+0.0;-0.0;0}°";
        Stage?.Invalidate();
    }

    private void Rotate(int quarterTurns)
    {
        _s.Rotate90 = (_s.Rotate90 + quarterTurns + 4) % 4;
        _s.Crop = new Rect(0, 0, 1, 1); // the frame changed shape
        if (AspectBox.SelectedIndex == 1) _aspect = null;
        ApplyAspect();
        UpdateSizeText();
        Stage.Invalidate();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-1);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(1);

    private void Flip_Click(object sender, RoutedEventArgs e)
    {
        _s.FlipH = FlipHToggle.IsChecked == true;
        _s.FlipV = FlipVToggle.IsChecked == true;
        Stage.Invalidate();
    }

    private void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        _s.Crop = new Rect(0, 0, 1, 1);
        _s.Rotate90 = 0;
        StraightenSlider.Value = 0;
        FlipHToggle.IsChecked = FlipVToggle.IsChecked = false;
        _s.FlipH = _s.FlipV = false;
        AspectBox.SelectedIndex = 0;
        UpdateSizeText();
        Stage.Invalidate();
    }

    private void Adjust_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        var slider = (Slider)sender;
        var name = slider.Name.Replace("Slider", string.Empty);
        slider.Header = e.NewValue == 0 ? name : $"{name} {e.NewValue:+0;-0}";
        _s.Exposure = (float)ExposureSlider.Value;
        _s.Contrast = (float)ContrastSlider.Value;
        _s.Highlights = (float)HighlightsSlider.Value;
        _s.Shadows = (float)ShadowsSlider.Value;
        _s.Warmth = (float)WarmthSlider.Value;
        _s.Saturation = (float)SaturationSlider.Value;
        Stage?.Invalidate();
    }

    private void ResetAdjust_Click(object sender, RoutedEventArgs e)
    {
        foreach (var slider in new[] { ExposureSlider, ContrastSlider, HighlightsSlider, ShadowsSlider, WarmthSlider, SaturationSlider })
            slider.Value = 0;
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (CompareToggle.IsChecked == true && _cropTab) Tab_Click(AdjustTab, e); // before/after shows the finished result
        Stage.Invalidate();
    }

    private void UpdateSizeText()
    {
        if (_preview == null) return;
        var ratio = _s.Crop.Width * Frame().Width / Math.Max(1, _s.Crop.Height * Frame().Height);
        SizeText.Text = _s.Crop == new Rect(0, 0, 1, 1) ? "Full frame" : $"Cropped to {_s.Crop.Width:P0} × {_s.Crop.Height:P0} · shape {ratio:0.##}:1";
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape: Close(); break;
            case (VirtualKey)219: Rotate(-1); break; // [
            case (VirtualKey)221: Rotate(1); break;  // ]
            case VirtualKey.B:
                CompareToggle.IsChecked = CompareToggle.IsChecked != true;
                Compare_Click(CompareToggle, e);
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ================= Saving =================

    private async void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_item.Path)!;
        var extension = EditRenderer.CanOverwrite(_item.Path) ? Path.GetExtension(_item.Path) : ".jpg";
        var stem = Path.GetFileNameWithoutExtension(_item.Path) + "_edited";
        var target = Path.Combine(folder, stem + extension);
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{stem} ({n}){extension}");
        await SaveAsync(target, overwrite: false);
    }

    private async void SaveOver_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Save over the original?",
            Content = "The edited photo takes the original's place. The original goes to the Recycle Bin, so you can still get it back.",
            PrimaryButtonText = "Save over original",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await SaveAsync(_item.Path, overwrite: true);
    }

    private async Task SaveAsync(string target, bool overwrite)
    {
        if (_saving || _preview == null) return;
        if (_s.IsEmpty) { StatusText.Text = "There are no edits to save yet."; return; }
        _saving = true;
        Busy.IsActive = true;
        SaveButton.IsEnabled = OverwriteButton.IsEnabled = false;
        StatusText.Text = "Saving at full resolution…";
        try
        {
            if (overwrite)
            {
                // Render to a temporary file first, so the original is only replaced once the edit is safely written.
                var temp = Path.Combine(Path.GetDirectoryName(target)!, $".ocula-edit-{Guid.NewGuid():N}{Path.GetExtension(target)}");
                await EditRenderer.SaveAsync(_device, _item, _s, temp);
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (!await Task.Run(() => DuplicateFinder.MoveToRecycleBin(new[] { target }, hwnd)))
                {
                    File.Delete(temp);
                    throw new IOException("The original couldn't be moved to the Recycle Bin. It may be open in another app.");
                }
                File.Move(temp, target);
            }
            else
            {
                await EditRenderer.SaveAsync(_device, _item, _s, target);
            }

            await _main.ShowAfterEditAsync(target);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't save: " + ex.Message;
            SaveButton.IsEnabled = true;
            OverwriteButton.IsEnabled = EditRenderer.CanOverwrite(_item.Path);
        }
        finally
        {
            _saving = false;
            Busy.IsActive = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
