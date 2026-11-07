using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Desktop.Services;

namespace CassiniFlow.Desktop.Localization;

/// <summary>
/// Adaptateur de localisation pour l'interface graphique Avalonia (CassiniFlow.Desktop).
/// Délégué directement vers le port <see cref="IAstroLocalization"/> du Domaine CassiniFlow.
/// Implémente INotifyPropertyChanged avec un indexeur pour permettre le rafraîchissement
/// dynamique et automatique des liaisons XAML via {loc:Tr MaCle}.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Notifier { get; } = new();

    private static IAstroLocalization _localization = new AstroLocalizationService();
    private static IUserSettingsService _userSettings = new JsonUserSettingsService();

    public static IAstroLocalization Localization
    {
        get => _localization;
        set
        {
            if (_localization == value) return;
            _localization.LanguageChanged -= OnDomainLanguageChanged;
            _localization = value ?? new AstroLocalizationService();
            _localization.LanguageChanged += OnDomainLanguageChanged;
            Notifier.RaisePropertyChanged();
            LanguageChanged?.Invoke();
        }
    }

    public static IUserSettingsService UserSettings
    {
        get => _userSettings;
        set => _userSettings = value ?? new JsonUserSettingsService();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? LanguageChanged;

    private static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, string>> Originals = new();

    static Loc()
    {
        _localization.LanguageChanged += OnDomainLanguageChanged;
    }

    private static void OnDomainLanguageChanged()
    {
        Notifier.RaisePropertyChanged();
        LanguageChanged?.Invoke();
    }

    public static AppLanguage Current
    {
        get => _localization.IsEnglish ? AppLanguage.English : AppLanguage.French;
        set => SetLanguage(value);
    }

    public static bool IsEnglish => _localization.IsEnglish;

    public string this[string key] => _localization.Get(key);

    public static string Get(string key) => _localization.Get(key);

    public static string Format(string key, params object?[] args) => _localization.Format(key, args);

    public static string Tr(string key) => _localization.Get(key);

    public static string Retranslate(string current) => _localization.Retranslate(current);

    public static string TranslateRuntime(string message) => _localization.Retranslate(message);

    public static void LoadSettings()
    {
        try
        {
            _userSettings.Load();
            SetLanguage(string.Equals(_userSettings.Current.Language, "en", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.English
                : AppLanguage.French, save: false);
        }
        catch
        {
            SetLanguage(AppLanguage.French, save: false);
        }
    }

    public static void SetLanguage(AppLanguage lang, bool save = true)
    {
        Notifier.SetLanguageInternal(lang, save);
    }

    public void SetLanguageInternal(AppLanguage lang, bool save = true)
    {
        var domainLang = lang == AppLanguage.English ? DomainLanguage.English : DomainLanguage.French;
        if (_localization.CurrentLanguage == domainLang) return;

        _localization.SetLanguage(domainLang);

        if (save)
        {
            _userSettings.UpdateLanguage(lang == AppLanguage.English ? "en" : "fr");
        }

        RaisePropertyChanged();
    }

    private void RaisePropertyChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>
    /// Met à jour les contrôles XAML dont le texte est encore statique (compatibilité).
    /// </summary>
    public static void LocalizeTree(ILogical root)
    {
        Localize((AvaloniaObject)root);
        foreach (var d in root.GetLogicalDescendants())
        {
            if (d is AvaloniaObject ao) Localize(ao);
        }
    }

    private static void Localize(AvaloniaObject o)
    {
        bool changed = false;

        if (o is TextBlock tb) changed |= Apply(o, TextBlock.TextProperty, tb.Text);
        if (o is TextBox box) changed |= Apply(o, TextBox.PlaceholderTextProperty, box.PlaceholderText);
        if (o is Window w) changed |= Apply(o, Window.TitleProperty, w.Title);
        if (o is MenuItem mi) changed |= Apply(o, MenuItem.HeaderProperty, mi.Header as string);
        else if (o is TabItem ti) changed |= Apply(o, TabItem.HeaderProperty, ti.Header as string);
        if (o is ContentControl cc && o is not TabItem) changed |= Apply(o, ContentControl.ContentProperty, cc.Content as string);
        if (o is Control c) changed |= Apply(o, ToolTip.TipProperty, ToolTip.GetTip(c) as string);

        if (changed && o is ComboBox cb && cb.SelectedItem is { } selected)
        {
            cb.SelectedItem = null;
            cb.SelectedItem = selected;
        }
    }

    private static bool Apply(AvaloniaObject o, AvaloniaProperty p, string? current)
    {
        var map = Originals.GetOrCreateValue(o);

        if (!map.TryGetValue(p, out var fr))
        {
            if (current is null) return false;
            fr = current;
            map[p] = fr;
        }

        string target = IsEnglish ? _localization.Retranslate(fr) : fr;
        if (string.Equals(current, target, StringComparison.Ordinal)) return false;

        o.SetValue(p, target);
        return true;
    }
}
