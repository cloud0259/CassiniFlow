using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CassiniFlow.Desktop.Controls;

public partial class EllipsisBounceLoader : UserControl
{
    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<EllipsisBounceLoader, IBrush?>(nameof(DotBrush));

    public static readonly StyledProperty<double> DotSizeProperty =
        AvaloniaProperty.Register<EllipsisBounceLoader, double>(nameof(DotSize), 7.0);

    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    public double DotSize
    {
        get => GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    public EllipsisBounceLoader()
    {
        InitializeComponent();
    }
}
