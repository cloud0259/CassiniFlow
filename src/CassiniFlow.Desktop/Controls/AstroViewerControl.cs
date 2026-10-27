using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Desktop.ViewModels;

namespace CassiniFlow.Desktop.Controls;

/// <summary>
/// Zero-lag frame viewer control.
/// Render() only draws the pre-blitted WriteableBitmap and vector overlays while preserving exact video aspect ratio
/// and mapping coordinates accurately between downscaled 4K previews and full-resolution video space.
/// </summary>
public sealed class AstroViewerControl : Control
{
    private WriteableBitmap? _bitmap;
    private AstroFrame? _renderedFrame;
    private DetectionResult? _renderedDetection;
    private double _previewScale = 1.0;
    private Rect _lastImageDestRect;
    private bool _isDragging;

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#14171d"));
    private static readonly IBrush BorderBrushColor = new SolidColorBrush(Color.Parse("#2b313d"));
    private static readonly IBrush TextMutedBrush = new SolidColorBrush(Color.Parse("#8b95a7"));
    private static readonly Pen CropPen = new(new SolidColorBrush(Color.Parse("#22c55e")), 1.6, new DashStyle([5, 4], 0));
    private static readonly Pen CrossPen = new(new SolidColorBrush(Color.Parse("#22c55e")), 1.4);
    private static readonly Pen AoiPen = new(new SolidColorBrush(Color.Parse("#eab308")), 1.5, new DashStyle([6, 4], 0));
    private static readonly Pen AfbPen = new(new SolidColorBrush(Color.Parse("#06b6d4")), 2.0);
    private static readonly Pen FrameBorderPen = new(BorderBrushColor, 1.0);

    public AstroViewerControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PreviewReady += OnPreviewReady;
        }
    }

    private void OnPreviewReady(AstroFrame? frame, DetectionResult? detection, double previewScale)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _renderedFrame = frame;
            _renderedDetection = detection;
            _previewScale = previewScale > 0 ? previewScale : 1.0;
            if (frame is not null && DataContext is MainWindowViewModel vm)
            {
                EnsureBitmapAndBlit(frame, detection?.Threshold8BitUsed ?? 40, vm.ShowDetectedPixelsRed);
            }
            InvalidateVisual();
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _isDragging = true;
        UpdateTargetFromPointer(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            UpdateTargetFromPointer(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
    }

    private void UpdateTargetFromPointer(Point pt)
    {
        if (DataContext is not MainWindowViewModel vm || _renderedFrame is null) return;
        if (_lastImageDestRect.Width <= 10 || _lastImageDestRect.Height <= 10) return;

        double relX = Math.Clamp((pt.X - _lastImageDestRect.X) / _lastImageDestRect.Width, 0.02, 0.98);
        double relY = Math.Clamp((pt.Y - _lastImageDestRect.Y) / _lastImageDestRect.Height, 0.02, 0.98);

        int fullW = vm.SelectedSourceFile?.Width ?? _renderedFrame.Width;
        int fullH = vm.SelectedSourceFile?.Height ?? _renderedFrame.Height;

        int imgX = (int)Math.Round(relX * fullW);
        int imgY = (int)Math.Round(relY * fullH);

        vm.SetAnchorOrCropTarget(imgX, imgY);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(BgBrush, bounds);

        if (DataContext is not MainWindowViewModel vm || _renderedFrame is null || _bitmap is null)
        {
            var ft = new FormattedText(
                "Aucun fichier vidéo chargé.\nUtilisez 'Ajouter des fichiers...' ou glissez-déposez une vidéo ici.",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                13,
                TextMutedBrush)
            {
                TextAlignment = TextAlignment.Center
            };
            context.DrawText(ft, new Point(Math.Max(10, (bounds.Width - ft.Width) / 2), Math.Max(10, (bounds.Height - ft.Height) / 2)));
            return;
        }

        double availW = Math.Max(16, bounds.Width - 16);
        double availH = Math.Max(16, bounds.Height - 16);
        double displayScale = Math.Min(availW / _renderedFrame.Width, availH / _renderedFrame.Height);
        double drawW = _renderedFrame.Width * displayScale;
        double drawH = _renderedFrame.Height * displayScale;
        double offsetX = (bounds.Width - drawW) * 0.5;
        double offsetY = (bounds.Height - drawH) * 0.5;

        _lastImageDestRect = new Rect(offsetX, offsetY, drawW, drawH);
        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), _lastImageDestRect);
        context.DrawRectangle(null, FrameBorderPen, _lastImageDestRect);

        // Combined scale from full-resolution source coordinates to screen coordinates
        double fullToScreenScale = displayScale * _previewScale;

        if (vm.ShowOverlayBoxes && !vm.PreviewCroppedOutput && _renderedDetection is not null)
        {
            if (vm.SelectedStabilisationModeIndex == 1) // Object / Planetary
            {
                double cx = offsetX + (_renderedDetection.CentroidX * displayScale) - (vm.CropOffsetX * fullToScreenScale);
                double cy = offsetY + (_renderedDetection.CentroidY * displayScale) - (vm.CropOffsetY * fullToScreenScale);

                if (vm.EnableCropping)
                {
                    double cw = vm.CropWidth * fullToScreenScale;
                    double ch = vm.CropHeight * fullToScreenScale;
                    if (vm.SelectedRotationIndex is 1 or 2) (cw, ch) = (ch, cw);
                    context.DrawRectangle(null, CropPen, new Rect(cx - cw / 2, cy - ch / 2, cw, ch));
                }

                context.DrawLine(CrossPen, new Point(cx - 10, cy), new Point(cx + 10, cy));
                context.DrawLine(CrossPen, new Point(cx, cy - 10), new Point(cx, cy + 10));
            }
            else if (vm.SelectedStabilisationModeIndex == 2) // Surface Feature (AFB)
            {
                double bx = offsetX + vm.TargetBoxX * fullToScreenScale;
                double by = offsetY + vm.TargetBoxY * fullToScreenScale;

                if (vm.EnableCropping)
                {
                    double cw = vm.CropWidth * fullToScreenScale;
                    double ch = vm.CropHeight * fullToScreenScale;
                    context.DrawRectangle(null, CropPen, new Rect(bx - cw / 2, by - ch / 2, cw, ch));
                }

                double aoiW = 160 * fullToScreenScale;
                double aoiH = 160 * fullToScreenScale;
                context.DrawRectangle(null, AoiPen, new Rect(bx - aoiW / 2, by - aoiH / 2, aoiW, aoiH));

                double afbW = vm.AfbSize * fullToScreenScale;
                double afbH = vm.AfbSize * fullToScreenScale;
                context.DrawRectangle(null, AfbPen, new Rect(bx - afbW / 2, by - afbH / 2, afbW, afbH));
            }
            else if (vm.EnableCropping)
            {
                double cx = offsetX + drawW * 0.5 - (vm.CropOffsetX * fullToScreenScale);
                double cy = offsetY + drawH * 0.5 - (vm.CropOffsetY * fullToScreenScale);
                double cw = vm.CropWidth * fullToScreenScale;
                double ch = vm.CropHeight * fullToScreenScale;
                if (vm.SelectedRotationIndex is 1 or 2) (cw, ch) = (ch, cw);
                context.DrawRectangle(null, CropPen, new Rect(cx - cw / 2, cy - ch / 2, cw, ch));
            }
        }
    }

    private unsafe void EnsureBitmapAndBlit(AstroFrame frame, int threshold8Bit, bool highlightRed)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using var fb = _bitmap.Lock();
        byte* ptr = (byte*)fb.Address;
        int stride = fb.RowBytes;
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        ushort thresh16 = (ushort)(threshold8Bit * 257);

        fixed (ushort* pData = frame.Data)
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = ptr + y * stride;
                int rowOffset = y * w;

                if (ch == 1)
                {
                    for (int x = 0; x < w; x++)
                    {
                        ushort v = pData[rowOffset + x];
                        byte g = (byte)(v >> 8);
                        int d = x * 4;
                        if (highlightRed && v >= thresh16)
                        {
                            row[d] = (byte)(g >> 2);
                            row[d + 1] = (byte)(g >> 2);
                            row[d + 2] = 255;
                            row[d + 3] = 255;
                        }
                        else
                        {
                            row[d] = g;
                            row[d + 1] = g;
                            row[d + 2] = g;
                            row[d + 3] = 255;
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < w; x++)
                    {
                        int s = (rowOffset + x) * 3;
                        ushort sr = pData[s], sg = pData[s + 1], sb = pData[s + 2];
                        byte r = (byte)(sr >> 8);
                        byte g = (byte)(sg >> 8);
                        byte b = (byte)(sb >> 8);

                        if (highlightRed)
                        {
                            ushort maxSample = sr >= sg ? (sr >= sb ? sr : sb) : (sg >= sb ? sg : sb);
                            if (maxSample >= thresh16)
                            {
                                r = 255;
                                g = (byte)(g >> 2);
                                b = (byte)(b >> 2);
                            }
                        }

                        int d = x * 4;
                        row[d] = b;
                        row[d + 1] = g;
                        row[d + 2] = r;
                        row[d + 3] = 255;
                    }
                }
            }
        }
    }
}
