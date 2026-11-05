using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace CassiniFlow.Desktop.Localization;

/// <summary>
/// Extension de balisage XAML permettant la syntaxe {loc:Tr MaCle}.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; }

    public TrExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = Loc.Notifier,
            Mode = BindingMode.OneWay
        };
    }
}
