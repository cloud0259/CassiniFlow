using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

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
    private static readonly Pen ActiveApPen = new(new SolidColorBrush(Color.Parse("#10b981")), 1.2);
    private static readonly Pen PrunedApPen = new(new SolidColorBrush(Color.FromArgb(50, 239, 68, 68)), 1.0);
    private static readonly Pen AnchorPen = new(new SolidColorBrush(Color.Parse("#fbbf24")), 1.8);

    public AstroViewerControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        InvalidateVisual();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PreviewReady += OnPreviewReady;
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName is nameof(MainWindowViewModel.IsAlignmentGridVisible)
                    or nameof(MainWindowViewModel.CurrentAlignmentGrid)
                    or nameof(MainWindowViewModel.ShowOverlayBoxes)
                    or nameof(MainWindowViewModel.PreviewCroppedOutput))
                {
                    InvalidateVisual();
                }
            };
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

        if (DataContext is not MainWindowViewModel vm || _renderedFrame is null)
            return;

        var pt = e.GetPosition(this);
        var pointerPoint = e.GetCurrentPoint(this);

        // Si la grille d'alignement est visible : clic gauche pour placer un AP, clic droit pour en retirer un
        if (vm.IsAlignmentGridVisible)
        {
            if (!_lastImageDestRect.Contains(pt))
                return;

            double displayScale = _lastImageDestRect.Width / _renderedFrame.Width;
            double localX = (pt.X - _lastImageDestRect.X) / displayScale;
            double localY = (pt.Y - _lastImageDestRect.Y) / displayScale;

            var (cropOffX, cropOffY) = GetGridCropOffset(vm, vm.CurrentAlignmentGrid);
            int gridX = (int)Math.Round(localX + cropOffX);
            int gridY = (int)Math.Round(localY + cropOffY);

            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                vm.AddManualAlignmentPoint(gridX, gridY);
                InvalidateVisual();
            }
            else if (pointerPoint.Properties.IsRightButtonPressed)
            {
                vm.RemoveManualAlignmentPoint(gridX, gridY);
                InvalidateVisual();
            }

            return; // Le cadre de crop ne bouge jamais lors de l'interaction avec les points d'alignement
        }

        // Si la grille d'alignement n'est pas active et que le mode Surface (AFB) est sélectionné
        if (vm.SelectedStabilisationModeIndex == 2)
        {
            _isDragging = true;
            UpdateTargetFromPointer(pt);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (DataContext is MainWindowViewModel vm)
        {
            Cursor = vm.IsAlignmentGridVisible
                ? new Cursor(StandardCursorType.Cross)
                : Cursor.Default;
        }

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
            DrawEmptyPlaceholder(context, bounds);
            return;
        }

        var (destRect, displayScale, fullToScreenScale) = CalculateLayout(bounds, _renderedFrame.Width, _renderedFrame.Height);
        _lastImageDestRect = destRect;

        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), destRect);
        context.DrawRectangle(null, FrameBorderPen, destRect);

        DrawOverlays(context, vm, destRect, displayScale, fullToScreenScale);
    }

    private void DrawEmptyPlaceholder(DrawingContext context, Rect bounds)
    {
        string prompt = Loc.Get("Viewer.NoVideoPrompt");
        var culture = Loc.IsEnglish ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture;

        var ft = new FormattedText(
            prompt,
            culture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            13,
            TextMutedBrush)
        {
            TextAlignment = TextAlignment.Center
        };

        double textX = Math.Max(10, (bounds.Width - ft.Width) / 2);
        double textY = Math.Max(10, (bounds.Height - ft.Height) / 2);
        context.DrawText(ft, new Point(textX, textY));
    }

    private (Rect DestRect, double DisplayScale, double FullToScreenScale) CalculateLayout(Rect bounds, int frameWidth, int frameHeight)
    {
        double availW = Math.Max(16, bounds.Width - 16);
        double availH = Math.Max(16, bounds.Height - 16);
        double displayScale = Math.Min(availW / frameWidth, availH / frameHeight);
        double drawW = frameWidth * displayScale;
        double drawH = frameHeight * displayScale;
        double offsetX = (bounds.Width - drawW) * 0.5;
        double offsetY = (bounds.Height - drawH) * 0.5;

        double fullToScreenScale = displayScale * _previewScale;
        return (new Rect(offsetX, offsetY, drawW, drawH), displayScale, fullToScreenScale);
    }

    private void DrawOverlays(
        DrawingContext context,
        MainWindowViewModel vm,
        Rect destRect,
        double displayScale,
        double fullToScreenScale)
    {
        if (vm.IsAlignmentGridVisible && vm.CurrentAlignmentGrid is not null && vm.CurrentAlignmentGrid.Points.Count > 0)
        {
            DrawAlignmentGridOverlay(context, vm, vm.CurrentAlignmentGrid, destRect, displayScale, fullToScreenScale);
        }

        if (!vm.ShowOverlayBoxes || vm.PreviewCroppedOutput || _renderedDetection is null)
            return;

        switch (vm.SelectedStabilisationModeIndex)
        {
            case 1: // Object / Planetary
                DrawPlanetaryOverlay(context, vm, _renderedDetection, destRect, displayScale, fullToScreenScale);
                break;

            case 2: // Surface Feature (AFB)
                DrawSurfaceFeatureOverlay(context, vm, destRect, fullToScreenScale);
                break;

            default: // None / manual crop
                if (vm.EnableCropping)
                {
                    DrawDefaultCropOverlay(context, vm, destRect, fullToScreenScale);
                }
                break;
        }
    }

    private (double CropStartX, double CropStartY) GetCropBoxOriginInFullFrame(MainWindowViewModel vm, int fullWidth, int fullHeight)
    {
        double cw = vm.CropWidth;
        double ch = vm.CropHeight;
        if (vm.SelectedRotationIndex is 1 or 2) (cw, ch) = (ch, cw);

        double targetX, targetY;
        switch (vm.SelectedStabilisationModeIndex)
        {
            case 1: // Planetary / Object
                targetX = (vm.CentreObjectInFrame && _renderedDetection is not null)
                    ? _renderedDetection.CentroidX
                    : fullWidth / 2.0;
                targetY = (vm.CentreObjectInFrame && _renderedDetection is not null)
                    ? _renderedDetection.CentroidY
                    : fullHeight / 2.0;
                break;

            case 2: // Surface Feature (AFB)
                targetX = vm.TargetBoxX;
                targetY = vm.TargetBoxY;
                break;

            default:
                targetX = fullWidth / 2.0;
                targetY = fullHeight / 2.0;
                break;
        }

        double startX = targetX - (cw / 2.0) - vm.CropOffsetX;
        double startY = targetY - (ch / 2.0) - vm.CropOffsetY;
        return (startX, startY);
    }

    private (double CropOffsetX, double CropOffsetY) GetGridCropOffset(MainWindowViewModel vm, AlignmentGrid? grid)
    {
        if (_renderedFrame is null)
            return (0, 0);

        if (!vm.EnableCropping)
            return (0, 0);

        bool isCurrentCropped = vm.PreviewCroppedOutput;
        int gridW = grid?.FrameWidth ?? 0;
        int gridH = grid?.FrameHeight ?? 0;

        // Une grille est en coordonnées recadrées si sa dimension correspond à la zone de crop ou n'est pas encore définie
        bool isGridInCropSpace = gridW <= 0 || (gridW <= vm.CropWidth + 2 && gridH <= vm.CropHeight + 2);

        if (isGridInCropSpace)
        {
            if (isCurrentCropped)
            {
                // Vue recadrée + Grille en coordonnées recadrées : pas de décalage
                return (0, 0);
            }
            else
            {
                // Vue plein cadre + Grille en coordonnées recadrées : on projette dans la boîte de crop
                var (startX, startY) = GetCropBoxOriginInFullFrame(vm, _renderedFrame.Width, _renderedFrame.Height);
                return (-startX, -startY);
            }
        }
        else
        {
            if (isCurrentCropped)
            {
                // Vue recadrée + Grille en plein cadre : translation vers l'origine du crop
                int fullW = vm.SelectedSourceFile?.Width ?? gridW;
                int fullH = vm.SelectedSourceFile?.Height ?? gridH;
                var (startX, startY) = GetCropBoxOriginInFullFrame(vm, fullW, fullH);
                return (startX, startY);
            }
            else
            {
                // Vue plein cadre + Grille en plein cadre : pas de décalage
                return (0, 0);
            }
        }
    }

    private void DrawAlignmentGridOverlay(
        DrawingContext context,
        MainWindowViewModel vm,
        AlignmentGrid grid,
        Rect destRect,
        double displayScale,
        double fullToScreenScale)
    {
        var (cropOffsetX, cropOffsetY) = GetGridCropOffset(vm, grid);

        foreach (var ap in grid.Points)
        {
            // Ne pas afficher les points élagués (ciel noir) pour éviter l'encombrement visuel
            if (!ap.IsActive)
                continue;

            double localX = ap.X - cropOffsetX;
            double localY = ap.Y - cropOffsetY;

            // Vérifier que le point se situe dans la zone de rendu active
            if (_renderedFrame is not null && (localX < 0 || localX > _renderedFrame.Width || localY < 0 || localY > _renderedFrame.Height))
                continue;

            double cx = destRect.X + (localX * displayScale);
            double cy = destRect.Y + (localY * displayScale);
            double halfSize = (ap.Size / 2.0) * displayScale;

            context.DrawRectangle(null, ActiveApPen, new Rect(cx - halfSize, cy - halfSize, halfSize * 2, halfSize * 2));
            context.DrawEllipse(ActiveApPen.Brush, null, new Point(cx, cy), 1.5, 1.5);
        }

        if (grid.AnchorPoint.X >= 0 && grid.AnchorPoint.Y >= 0)
        {
            double localAnchorX = grid.AnchorPoint.X - cropOffsetX;
            double localAnchorY = grid.AnchorPoint.Y - cropOffsetY;

            if (_renderedFrame is null || (localAnchorX >= 0 && localAnchorX <= _renderedFrame.Width && localAnchorY >= 0 && localAnchorY <= _renderedFrame.Height))
            {
                double ax = destRect.X + (localAnchorX * displayScale);
                double ay = destRect.Y + (localAnchorY * displayScale);
                context.DrawEllipse(null, AnchorPen, new Point(ax, ay), 6, 6);
                context.DrawEllipse(AnchorPen.Brush, null, new Point(ax, ay), 2, 2);
            }
        }
    }

    private static void DrawPlanetaryOverlay(
        DrawingContext context,
        MainWindowViewModel vm,
        DetectionResult detection,
        Rect destRect,
        double displayScale,
        double fullToScreenScale)
    {
        double cx = destRect.X + (detection.CentroidX * displayScale) - (vm.CropOffsetX * fullToScreenScale);
        double cy = destRect.Y + (detection.CentroidY * displayScale) - (vm.CropOffsetY * fullToScreenScale);

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

    private static void DrawSurfaceFeatureOverlay(
        DrawingContext context,
        MainWindowViewModel vm,
        Rect destRect,
        double fullToScreenScale)
    {
        double bx = destRect.X + vm.TargetBoxX * fullToScreenScale;
        double by = destRect.Y + vm.TargetBoxY * fullToScreenScale;

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

    private static void DrawDefaultCropOverlay(
        DrawingContext context,
        MainWindowViewModel vm,
        Rect destRect,
        double fullToScreenScale)
    {
        double cx = destRect.X + destRect.Width * 0.5 - (vm.CropOffsetX * fullToScreenScale);
        double cy = destRect.Y + destRect.Height * 0.5 - (vm.CropOffsetY * fullToScreenScale);
        double cw = vm.CropWidth * fullToScreenScale;
        double ch = vm.CropHeight * fullToScreenScale;
        if (vm.SelectedRotationIndex is 1 or 2) (cw, ch) = (ch, cw);
        context.DrawRectangle(null, CropPen, new Rect(cx - cw / 2, cy - ch / 2, cw, ch));
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
        ushort thresh16 = (ushort)(threshold8Bit * 257);

        fixed (ushort* pData = frame.Data)
        {
            switch (frame.Channels)
            {
                case 1:
                    BlitMonochrome(pData, ptr, w, h, stride, thresh16, highlightRed);
                    break;
                case 3:
                default:
                    BlitColorRgb(pData, ptr, w, h, stride, thresh16, highlightRed);
                    break;
            }
        }
    }

    private static unsafe void BlitMonochrome(
        ushort* pData,
        byte* ptr,
        int w,
        int h,
        int stride,
        ushort thresh16,
        bool highlightRed)
    {
        for (int y = 0; y < h; y++)
        {
            byte* row = ptr + y * stride;
            int rowOffset = y * w;

            if (highlightRed)
            {
                for (int x = 0; x < w; x++)
                {
                    ushort v = pData[rowOffset + x];
                    byte g = (byte)(v >> 8);
                    int d = x * 4;
                    if (v >= thresh16)
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
                    byte g = (byte)(pData[rowOffset + x] >> 8);
                    int d = x * 4;
                    row[d] = g;
                    row[d + 1] = g;
                    row[d + 2] = g;
                    row[d + 3] = 255;
                }
            }
        }
    }

    private static unsafe void BlitColorRgb(
        ushort* pData,
        byte* ptr,
        int w,
        int h,
        int stride,
        ushort thresh16,
        bool highlightRed)
    {
        for (int y = 0; y < h; y++)
        {
            byte* row = ptr + y * stride;
            int rowOffset = y * w;

            if (highlightRed)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = (rowOffset + x) * 3;
                    ushort sr = pData[s], sg = pData[s + 1], sb = pData[s + 2];
                    byte r = (byte)(sr >> 8);
                    byte g = (byte)(sg >> 8);
                    byte b = (byte)(sb >> 8);

                    ushort maxSample = sr >= sg ? (sr >= sb ? sr : sb) : (sg >= sb ? sg : sb);
                    if (maxSample >= thresh16)
                    {
                        r = 255;
                        g = (byte)(g >> 2);
                        b = (byte)(b >> 2);
                    }

                    int d = x * 4;
                    row[d] = b;
                    row[d + 1] = g;
                    row[d + 2] = r;
                    row[d + 3] = 255;
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    int s = (rowOffset + x) * 3;
                    int d = x * 4;
                    row[d] = (byte)(pData[s + 2] >> 8);     // b
                    row[d + 1] = (byte)(pData[s + 1] >> 8); // g
                    row[d + 2] = (byte)(pData[s] >> 8);     // r
                    row[d + 3] = 255;
                }
            }
        }
    }
}
