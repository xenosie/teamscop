using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Teamscop.App.Views;

public partial class PhotoCropWindow : Window
{
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private Point? _dragStart;
    private Vector _originTranslate;

    public byte[]? CroppedPng { get; private set; }
    public Bitmap? CroppedBitmap { get; private set; }

    public PhotoCropWindow()
    {
        InitializeComponent();
        PreviewImage.RenderTransform = new TransformGroup
        {
            Children = { _scale, _translate }
        };
        PreviewImage.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        ZoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _scale.ScaleX = ZoomSlider.Value;
                _scale.ScaleY = ZoomSlider.Value;
            }
        };
        CropHost.PointerPressed += OnPointerPressed;
        CropHost.PointerMoved += OnPointerMoved;
        CropHost.PointerReleased += (_, _) => _dragStart = null;
        CropHost.PointerWheelChanged += OnWheel;
        BtnCancel.Click += (_, _) => Close(false);
        BtnApply.Click += (_, _) => ApplyAndClose();
    }

    public void LoadBitmap(Bitmap bitmap)
    {
        PreviewImage.Source = bitmap;
        _scale.ScaleX = 1;
        _scale.ScaleY = 1;
        _translate.X = 0;
        _translate.Y = 0;
        ZoomSlider.Value = 1;
    }

    public static async Task<(byte[] Png, Bitmap Preview)?> PickAndCropAsync(Window owner)
    {
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose photo",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
                    }
                ]
            });

            if (files.Count == 0)
            {
                return null;
            }

            await using var stream = await files[0].OpenReadAsync();
            var bitmap = new Bitmap(stream);
            var dlg = new PhotoCropWindow();
            dlg.LoadBitmap(bitmap);
            var ok = await dlg.ShowDialog<bool>(owner);
            if (!ok || dlg.CroppedPng is null || dlg.CroppedBitmap is null)
            {
                return null;
            }

            return (dlg.CroppedPng, dlg.CroppedBitmap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Could not load that image.", ex);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = e.GetPosition(CropHost);
        _originTranslate = new Vector(_translate.X, _translate.Y);
        e.Pointer.Capture(CropHost);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is null || !e.GetCurrentPoint(CropHost).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var pos = e.GetPosition(CropHost);
        var delta = pos - _dragStart.Value;
        _translate.X = _originTranslate.X + delta.X;
        _translate.Y = _originTranslate.Y + delta.Y;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + e.Delta.Y * 0.15, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    private void ApplyAndClose()
    {
        const int size = 320;
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        rtb.Render(CropHost);

        // Mask to a circle (transparent outside) so uploads are round.
        var circular = MaskToCircle(rtb, size);
        using var ms = new MemoryStream();
        circular.Save(ms);
        CroppedPng = ms.ToArray();
        CroppedBitmap = circular;
        Close(true);
    }

    private static WriteableBitmap MaskToCircle(RenderTargetBitmap source, int size)
    {
        using var encoded = new MemoryStream();
        source.Save(encoded);
        encoded.Position = 0;
        using var decoded = WriteableBitmap.Decode(encoded);

        var wb = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using var srcBuf = decoded.Lock();
        using var dstBuf = wb.Lock();
        var srcStride = srcBuf.RowBytes;
        var dstStride = dstBuf.RowBytes;
        var srcBytes = new byte[srcStride * size];
        var dstBytes = new byte[dstStride * size];
        Marshal.Copy(srcBuf.Address, srcBytes, 0, Math.Min(srcBytes.Length, srcStride * srcBuf.Size.Height));

        var radius = (size - 1) / 2.0;
        var cx = (size - 1) / 2.0;
        var cy = (size - 1) / 2.0;
        var r2 = radius * radius;
        var copyH = Math.Min(size, srcBuf.Size.Height);
        var copyW = Math.Min(size, srcBuf.Size.Width);

        for (var y = 0; y < copyH; y++)
        {
            var srcOff = y * srcStride;
            var dstOff = y * dstStride;
            for (var x = 0; x < copyW; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if ((dx * dx) + (dy * dy) > r2)
                {
                    continue;
                }

                var si = srcOff + (x * 4);
                var di = dstOff + (x * 4);
                dstBytes[di] = srcBytes[si];
                dstBytes[di + 1] = srcBytes[si + 1];
                dstBytes[di + 2] = srcBytes[si + 2];
                dstBytes[di + 3] = srcBytes[si + 3];
            }
        }

        Marshal.Copy(dstBytes, 0, dstBuf.Address, dstBytes.Length);
        return wb;
    }
}

