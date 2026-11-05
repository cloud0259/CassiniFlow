using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Desktop.Controls;

/// <summary>
/// High-performance GPU vector timeline control for CassiniFlow.
/// Renders multi-algorithm quality curves, seeing turbulence bands, In/Out selection brackets,
/// and interactive playback scrubber with zero allocation during mouse dragging.
/// </summary>
public sealed class AstroQualityTimelineControl : Control
{
    public static readonly StyledProperty<QualityTimelineDataSet?> TimelineDataProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, QualityTimelineDataSet?>(nameof(TimelineData));

    public static readonly StyledProperty<int> CurrentFrameIndexProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, int>(nameof(CurrentFrameIndex), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> MaxFrameCountProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, int>(nameof(MaxFrameCount), 1);

    public static readonly StyledProperty<int> SelectionStartProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, int>(nameof(SelectionStart), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> SelectionEndProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, int>(nameof(SelectionEnd), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> CutoffPercentProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, int>(nameof(CutoffPercent), 100, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsChronologicalModeProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, bool>(nameof(IsChronologicalMode), true);

    public static readonly StyledProperty<bool> ShowGradientSharpnessProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, bool>(nameof(ShowGradientSharpness), true);

    public static readonly StyledProperty<bool> ShowNinoxProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, bool>(nameof(ShowNinox), true);

    public static readonly StyledProperty<bool> ShowPeakHistogramProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, bool>(nameof(ShowPeakHistogram), false);

    public static readonly StyledProperty<bool> ShowTotalBrightnessProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, bool>(nameof(ShowTotalBrightness), false);

    public static readonly StyledProperty<bool> IsAnalyzingProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, bool>(nameof(IsAnalyzing), false);

    public static readonly StyledProperty<double> AnalysisProgressProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, double>(nameof(AnalysisProgress), 0.0);

    public static readonly StyledProperty<int> HoverFrameIndexProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, int>(nameof(HoverFrameIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> HoverScoreProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, double>(nameof(HoverScore), 0.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> HoverRankProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, int>(nameof(HoverRank), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsHoveredProperty =
        AvaloniaProperty.Register<AstroQualityTimelineControl, bool>(nameof(IsHovered), false, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public QualityTimelineDataSet? TimelineData
    {
        get => GetValue(TimelineDataProperty);
        set => SetValue(TimelineDataProperty, value);
    }

    public int CurrentFrameIndex
    {
        get => GetValue(CurrentFrameIndexProperty);
        set => SetValue(CurrentFrameIndexProperty, value);
    }

    public int MaxFrameCount
    {
        get => GetValue(MaxFrameCountProperty);
        set => SetValue(MaxFrameCountProperty, value);
    }

    public int SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public int CutoffPercent
    {
        get => GetValue(CutoffPercentProperty);
        set => SetValue(CutoffPercentProperty, value);
    }

    public bool IsChronologicalMode
    {
        get => GetValue(IsChronologicalModeProperty);
        set => SetValue(IsChronologicalModeProperty, value);
    }

    public bool ShowGradientSharpness
    {
        get => GetValue(ShowGradientSharpnessProperty);
        set => SetValue(ShowGradientSharpnessProperty, value);
    }

    public bool ShowNinox
    {
        get => GetValue(ShowNinoxProperty);
        set => SetValue(ShowNinoxProperty, value);
    }

    public bool ShowPeakHistogram
    {
        get => GetValue(ShowPeakHistogramProperty);
        set => SetValue(ShowPeakHistogramProperty, value);
    }

    public bool ShowTotalBrightness
    {
        get => GetValue(ShowTotalBrightnessProperty);
        set => SetValue(ShowTotalBrightnessProperty, value);
    }

    public bool IsAnalyzing
    {
        get => GetValue(IsAnalyzingProperty);
        set => SetValue(IsAnalyzingProperty, value);
    }

    public double AnalysisProgress
    {
        get => GetValue(AnalysisProgressProperty);
        set => SetValue(AnalysisProgressProperty, value);
    }

    public int HoverFrameIndex
    {
        get => GetValue(HoverFrameIndexProperty);
        set => SetValue(HoverFrameIndexProperty, value);
    }

    public double HoverScore
    {
        get => GetValue(HoverScoreProperty);
        set => SetValue(HoverScoreProperty, value);
    }

    public int HoverRank
    {
        get => GetValue(HoverRankProperty);
        set => SetValue(HoverRankProperty, value);
    }

    public bool IsHovered
    {
        get => GetValue(IsHoveredProperty);
        set => SetValue(IsHoveredProperty, value);
    }

    // Static brushes & pens
    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#080c14"));
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.Parse("#1e293b"));
    private static readonly IBrush DimExclusionBrush = new SolidColorBrush(Color.FromArgb(170, 3, 7, 18));
    private static readonly IBrush LowTurbulenceBrush = new SolidColorBrush(Color.FromArgb(28, 16, 185, 129));
    private static readonly IBrush ModTurbulenceBrush = new SolidColorBrush(Color.FromArgb(18, 245, 158, 11));
    private static readonly IBrush HighTurbulenceBrush = new SolidColorBrush(Color.FromArgb(32, 239, 68, 68));

    private static readonly Pen ScrubberPen = new(new SolidColorBrush(Color.Parse("#38bdf8")), 2.0);
    private static readonly Pen InHandlePen = new(new SolidColorBrush(Color.Parse("#10b981")), 2.0);
    private static readonly Pen OutHandlePen = new(new SolidColorBrush(Color.Parse("#ef4444")), 2.0);
    private static readonly Pen CutoffPen = new(new SolidColorBrush(Color.FromArgb(160, 56, 189, 248)), 1.2, new DashStyle([4, 4], 0));
    private static readonly Pen GridLinePen = new(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 0.8, new DashStyle([2, 4], 0));

    private static readonly Pen GradPen = new(new SolidColorBrush(Color.Parse("#38bdf8")), 1.8);
    private static readonly Pen NinoxPen = new(new SolidColorBrush(Color.Parse("#c084fc")), 1.4);
    private static readonly Pen HistoPen = new(new SolidColorBrush(Color.Parse("#fbbf24")), 1.4);
    private static readonly Pen TotalPen = new(new SolidColorBrush(Color.Parse("#34d399")), 1.2);

    private static readonly Typeface MonospaceTypeface = new("Cascadia Code, Consolas, Courier New, monospace");

    // Static cursors for zero-alloc pointer feedback
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor SizeWeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor DragCursor = SizeWeCursor;

    private enum DragMode { None, Scrubber, HandleIn, HandleOut }
    private DragMode _activeDrag = DragMode.None;

    static AstroQualityTimelineControl()
    {
        AffectsRender<AstroQualityTimelineControl>(
            TimelineDataProperty,
            CurrentFrameIndexProperty,
            MaxFrameCountProperty,
            SelectionStartProperty,
            SelectionEndProperty,
            CutoffPercentProperty,
            IsChronologicalModeProperty,
            ShowGradientSharpnessProperty,
            ShowNinoxProperty,
            ShowPeakHistogramProperty,
            ShowTotalBrightnessProperty,
            IsAnalyzingProperty,
            AnalysisProgressProperty);
    }

    public AstroQualityTimelineControl()
    {
        ClipToBounds = true;
        MinHeight = 44;
        Cursor = HandCursor;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 8 || h <= 8) return;

        var fullRect = new Rect(0, 0, w, h);
        context.DrawRectangle(BgBrush, new Pen(BorderBrush, 1.0), fullRect);

        int total = Math.Max(1, MaxFrameCount);
        var data = TimelineData;

        // Draw horizontal grid lines (25%, 50%, 75%)
        for (int p = 25; p <= 75; p += 25)
        {
            double gy = h - (p / 100.0) * (h - 16) - 8;
            context.DrawLine(GridLinePen, new Point(0, gy), new Point(w, gy));
        }

        // 1. Draw Turbulence bands (if chronological mode)
        if (IsChronologicalMode && data != null && data.TurbulenceZones.Count > 0)
        {
            foreach (var zone in data.TurbulenceZones)
            {
                double x0 = (double)zone.StartFrame / total * w;
                double x1 = (double)(zone.EndFrame + 1) / total * w;
                var brush = zone.Level switch
                {
                    TurbulenceLevel.Low => LowTurbulenceBrush,
                    TurbulenceLevel.High => HighTurbulenceBrush,
                    _ => ModTurbulenceBrush
                };
                context.DrawRectangle(brush, null, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
            }
        }

        // 2. Draw Out-of-bounds dimmed exclusion zones (outside In..Out) in chronological mode
        if (IsChronologicalMode)
        {
            int selIn = Math.Clamp(SelectionStart, 1, total);
            int selOut = Math.Clamp(SelectionEnd, selIn, total);

            double xIn = ((double)(selIn - 1) / total) * w;
            double xOut = ((double)selOut / total) * w;

            if (xIn > 1)
            {
                context.DrawRectangle(DimExclusionBrush, null, new Rect(0, 0, xIn, h));
            }
            if (xOut < w - 1)
            {
                context.DrawRectangle(DimExclusionBrush, null, new Rect(xOut, 0, w - xOut, h));
            }
        }

        // 3. Draw Cutoff threshold line ("Keep X% best")
        if (CutoffPercent < 100 && data != null)
        {
            var primaryRecords = data.GetSortedScores(data.PrimaryAlgorithm);
            if (primaryRecords != null && primaryRecords.Length > 0)
            {
                int cutoffIndex = Math.Clamp((int)Math.Round(primaryRecords.Length * (CutoffPercent / 100.0)), 0, primaryRecords.Length - 1);
                double cutoffNorm = primaryRecords[cutoffIndex].NormalizedScore;
                double cy = h - (cutoffNorm / 100.0) * (h - 16) - 8;

                context.DrawLine(CutoffPen, new Point(0, cy), new Point(w, cy));
                var ft = new FormattedText(
                    $"{CutoffPercent}%",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonospaceTypeface,
                    9.5,
                    new SolidColorBrush(Color.Parse("#38bdf8")));
                context.DrawText(ft, new Point(w - ft.Width - 4, Math.Clamp(cy - ft.Height - 1, 2, h - ft.Height - 2)));
            }
        }

        // 4. Draw Curves for algorithms
        if (data != null)
        {
            // Dessine toujours obligatoirement l'algorithme principal actif du dataset
            var primaryPen = data.PrimaryAlgorithm switch
            {
                QualityAlgorithm.OriginalNinox => NinoxPen,
                QualityAlgorithm.PeakHistogram => HistoPen,
                QualityAlgorithm.TotalBrightness => TotalPen,
                _ => GradPen
            };
            DrawAlgorithmCurve(context, data, data.PrimaryAlgorithm, primaryPen, w, h, total);

            // Dessine les courbes optionnelles activées si elles existent et diffèrent de l'algorithme principal
            if (ShowGradientSharpness && data.PrimaryAlgorithm != QualityAlgorithm.GradientSharpness && data.HasScoresFor(QualityAlgorithm.GradientSharpness))
                DrawAlgorithmCurve(context, data, QualityAlgorithm.GradientSharpness, GradPen, w, h, total);
            if (ShowNinox && data.PrimaryAlgorithm != QualityAlgorithm.OriginalNinox && data.HasScoresFor(QualityAlgorithm.OriginalNinox))
                DrawAlgorithmCurve(context, data, QualityAlgorithm.OriginalNinox, NinoxPen, w, h, total);
            if (ShowPeakHistogram && data.PrimaryAlgorithm != QualityAlgorithm.PeakHistogram && data.HasScoresFor(QualityAlgorithm.PeakHistogram))
                DrawAlgorithmCurve(context, data, QualityAlgorithm.PeakHistogram, HistoPen, w, h, total);
            if (ShowTotalBrightness && data.PrimaryAlgorithm != QualityAlgorithm.TotalBrightness && data.HasScoresFor(QualityAlgorithm.TotalBrightness))
                DrawAlgorithmCurve(context, data, QualityAlgorithm.TotalBrightness, TotalPen, w, h, total);
        }

        // 5. Draw IN and OUT handles (in Chronological mode, seulement si des données sont présentes)
        if (IsChronologicalMode && data != null)
        {
            int selIn = Math.Clamp(SelectionStart, 1, total);
            int selOut = Math.Clamp(SelectionEnd, selIn, total);
            double xIn = ((double)(selIn - 1) / total) * w;
            double xOut = ((double)selOut / total) * w;

            // In handle (Green)
            context.DrawLine(InHandlePen, new Point(xIn, 0), new Point(xIn, h));
            DrawHandleBracket(context, xIn, isLeft: true, InHandlePen.Brush);

            // Out handle (Red)
            context.DrawLine(OutHandlePen, new Point(xOut, 0), new Point(xOut, h));
            DrawHandleBracket(context, xOut, isLeft: false, OutHandlePen.Brush);
        }

        // 7. Draw Current Frame Scrubber Line (seulement si des données sont présentes)
        if (data != null)
        {
            double scrubX = GetScrubberX(w, total, data);
            context.DrawLine(ScrubberPen, new Point(scrubX, 0), new Point(scrubX, h));
            DrawScrubberHead(context, scrubX, ScrubberPen.Brush);
        }
    }

    private readonly Dictionary<(QualityAlgorithm Algo, bool Chrono, int W, int H), StreamGeometry> _cachedGeometries = new();
    private QualityTimelineDataSet? _cachedData;

    private void DrawAlgorithmCurve(
        DrawingContext context,
        QualityTimelineDataSet data,
        QualityAlgorithm algo,
        Pen pen,
        double width,
        double height,
        int totalFrames)
    {
        if (!ReferenceEquals(_cachedData, data))
        {
            _cachedGeometries.Clear();
            _cachedData = data;
        }

        var key = (algo, IsChronologicalMode, (int)Math.Round(width), (int)Math.Round(height));
        if (_cachedGeometries.TryGetValue(key, out var cachedGeom))
        {
            context.DrawGeometry(null, pen, cachedGeom);
            return;
        }

        var records = IsChronologicalMode ? data.GetScores(algo) : data.GetSortedScores(algo);
        if (records == null || records.Length == 0) return;

        int len = records.Length;
        // Optimization: adaptive stride so we never draw more points than pixels
        int stride = Math.Max(1, len / (int)Math.Max(100, width));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool started = false;
            for (int i = 0; i < len; i += stride)
            {
                double norm = records[i].NormalizedScore;
                double x = (double)i / (len - 1) * width;
                double y = height - (norm / 100.0) * (height - 16) - 8;

                if (!started)
                {
                    ctx.BeginFigure(new Point(x, y), isFilled: false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(new Point(x, y));
                }
            }
            // Ensure last point is drawn
            if (len > 1 && (len - 1) % stride != 0)
            {
                double norm = records[len - 1].NormalizedScore;
                double x = width;
                double y = height - (norm / 100.0) * (height - 16) - 8;
                ctx.LineTo(new Point(x, y));
            }
        }

        _cachedGeometries[key] = geometry;
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawHandleBracket(DrawingContext context, double x, bool isLeft, IBrush? brush)
    {
        if (brush == null) return;
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            if (isLeft)
            {
                ctx.BeginFigure(new Point(x, 1), isFilled: true);
                ctx.LineTo(new Point(x + 7, 1));
                ctx.LineTo(new Point(x, 9));
            }
            else
            {
                ctx.BeginFigure(new Point(x, 1), isFilled: true);
                ctx.LineTo(new Point(x - 7, 1));
                ctx.LineTo(new Point(x, 9));
            }
        }
        context.DrawGeometry(brush, null, geom);
    }

    private static void DrawScrubberHead(DrawingContext context, double x, IBrush? brush)
    {
        if (brush == null) return;
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(new Point(x - 4, 0), isFilled: true);
            ctx.LineTo(new Point(x + 4, 0));
            ctx.LineTo(new Point(x + 4, 4));
            ctx.LineTo(new Point(x, 8));
            ctx.LineTo(new Point(x - 4, 4));
        }
        context.DrawGeometry(brush, null, geom);
    }

    private double GetScrubberX(double width, int totalFrames, QualityTimelineDataSet? data)
    {
        if (IsChronologicalMode || data == null)
        {
            return Math.Clamp(((double)(CurrentFrameIndex - 1) / Math.Max(1, totalFrames)) * width, 0, width);
        }

        var sorted = data.GetSortedScores(data.PrimaryAlgorithm);
        if (sorted != null && sorted.Length > 0)
        {
            int curr = CurrentFrameIndex - 1;
            for (int i = 0; i < sorted.Length; i++)
            {
                if (sorted[i].FrameIndex == curr)
                {
                    return Math.Clamp(((double)i / (sorted.Length - 1)) * width, 0, width);
                }
            }
        }

        return Math.Clamp(((double)(CurrentFrameIndex - 1) / Math.Max(1, totalFrames)) * width, 0, width);
    }

    private int GetFrameIndexFromX(double x)
    {
        double w = Bounds.Width;
        if (w <= 0) return 1;

        int total = Math.Max(1, MaxFrameCount);
        double pct = Math.Clamp(x / w, 0.0, 1.0);

        if (IsChronologicalMode || TimelineData == null)
        {
            return Math.Clamp((int)Math.Round(pct * (total - 1)) + 1, 1, total);
        }

        var sorted = TimelineData.GetSortedScores(TimelineData.PrimaryAlgorithm);
        if (sorted != null && sorted.Length > 0)
        {
            int sortedPos = Math.Clamp((int)Math.Round(pct * (sorted.Length - 1)), 0, sorted.Length - 1);
            return sorted[sortedPos].FrameIndex + 1;
        }

        return Math.Clamp((int)Math.Round(pct * (total - 1)) + 1, 1, total);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;

        double x = pt.Position.X;
        double w = Bounds.Width;
        int total = Math.Max(1, MaxFrameCount);

        // 1. Priorité au curseur de l'image (Scrubber)
        double scrubX = GetScrubberX(w, total, TimelineData);
        if (Math.Abs(x - scrubX) <= 6)
        {
            _activeDrag = DragMode.Scrubber;
            Cursor = SizeWeCursor;
            e.Pointer.Capture(this);
            CurrentFrameIndex = GetFrameIndexFromX(x);
            return;
        }

        // 2. Poignées de sélection temporelle IN et OUT (mode chronologique)
        if (IsChronologicalMode)
        {
            double xIn = ((double)(SelectionStart - 1) / total) * w;
            double xOut = ((double)SelectionEnd / total) * w;

            if (Math.Abs(x - xIn) <= 8)
            {
                _activeDrag = DragMode.HandleIn;
                Cursor = SizeWeCursor;
                e.Pointer.Capture(this);
                return;
            }
            if (Math.Abs(x - xOut) <= 8)
            {
                _activeDrag = DragMode.HandleOut;
                Cursor = SizeWeCursor;
                e.Pointer.Capture(this);
                return;
            }
        }

        // 3. Clic direct ailleurs : déplace la frame courante
        _activeDrag = DragMode.Scrubber;
        Cursor = SizeWeCursor;
        e.Pointer.Capture(this);
        CurrentFrameIndex = GetFrameIndexFromX(x);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetCurrentPoint(this);
        double x = pt.Position.X;
        double w = Bounds.Width;
        int total = Math.Max(1, MaxFrameCount);

        switch (_activeDrag)
        {
            case DragMode.Scrubber:
                Cursor = SizeWeCursor;
                CurrentFrameIndex = GetFrameIndexFromX(x);
                break;
            case DragMode.HandleIn:
            {
                Cursor = SizeWeCursor;
                int frame = Math.Clamp(GetFrameIndexFromX(x), 1, Math.Max(1, SelectionEnd - 1));
                SelectionStart = frame;
                break;
            }
            case DragMode.HandleOut:
            {
                Cursor = SizeWeCursor;
                int frame = Math.Clamp(GetFrameIndexFromX(x), Math.Min(total, SelectionStart + 1), total);
                SelectionEnd = frame;
                break;
            }
        }

        // Update hover metadata for tooltip
        if (pt.Position.X >= 0 && pt.Position.X <= w && pt.Position.Y >= 0 && pt.Position.Y <= Bounds.Height)
        {
            int hFrame = GetFrameIndexFromX(x);
            HoverFrameIndex = hFrame;
            IsHovered = true;

            if (TimelineData != null)
            {
                var rec = TimelineData.GetFrameRecord(TimelineData.PrimaryAlgorithm, hFrame - 1);
                if (rec.HasValue)
                {
                    HoverScore = rec.Value.NormalizedScore;
                    HoverRank = rec.Value.Rank;
                }
            }
        }
        else
        {
            IsHovered = false;
        }

        // Update cursor icon when approaching handles or the current frame scrubber
        if (_activeDrag == DragMode.None)
        {
            double scrubX = GetScrubberX(w, total, TimelineData);
            bool overScrubber = Math.Abs(x - scrubX) <= 6;

            if (IsChronologicalMode)
            {
                double xIn = ((double)(SelectionStart - 1) / total) * w;
                double xOut = ((double)SelectionEnd / total) * w;
                if (Math.Abs(x - xIn) <= 6 || Math.Abs(x - xOut) <= 6 || overScrubber)
                {
                    Cursor = SizeWeCursor;
                }
                else
                {
                    Cursor = HandCursor;
                }
            }
            else
            {
                Cursor = overScrubber ? SizeWeCursor : HandCursor;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _activeDrag = DragMode.None;
        Cursor = HandCursor;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _activeDrag = DragMode.None;
        Cursor = HandCursor;
    }
}
