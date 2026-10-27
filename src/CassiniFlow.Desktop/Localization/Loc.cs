using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;

namespace CassiniFlow.Desktop.Localization;

public enum AppLanguage
{
    French,
    English
}

/// <summary>
/// Gestionnaire central de localisation FR / EN basé sur des fichiers JSON structurés.
/// Implémente INotifyPropertyChanged avec un indexeur pour permettre le rafraîchissement
/// dynamique et automatique des liaisons XAML via {loc:Tr MaCle}.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? LanguageChanged;

    private static readonly Dictionary<string, string> FrStrings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> EnStrings = new(StringComparer.OrdinalIgnoreCase);

    // Dictionnaires de correspondance directe FR <-> EN pour compatibilité ascendante
    private static readonly Dictionary<string, string> FrToEn = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> EnToFr = new(StringComparer.Ordinal);

    private static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, string>> Originals = new();

    private AppLanguage _current = AppLanguage.French;

    public static AppLanguage Current
    {
        get => Instance._current;
        set => SetLanguage(value);
    }

    public static bool IsEnglish => Instance._current == AppLanguage.English;

    static Loc()
    {
        LoadLanguages();
        BuildDirectLookupMaps();
    }

    private Loc()
    {
    }

    /// <summary>Indexeur permettant la liaison XAML : {Binding [Key], Source={x:Static loc:Loc.Instance}}</summary>
    public string this[string key] => Get(key);

    /// <summary>Obtient la traduction associée à la clé donnée selon la langue active.</summary>
    public static string Get(string key)
    {
        var dict = IsEnglish ? EnStrings : FrStrings;
        if (dict.TryGetValue(key, out var val))
            return val;

        // Fallback si la clé n'existe pas en anglais, tenter le français
        if (IsEnglish && FrStrings.TryGetValue(key, out val))
            return val;

        // Si la chaîne est elle-même un texte français connu
        if (IsEnglish && FrToEn.TryGetValue(key, out val))
            return val;

        return key;
    }

    /// <summary>Formate une chaîne traduite avec les arguments fournis.</summary>
    public static string Format(string key, params object?[] args)
    {
        string pattern = Get(key);
        try
        {
            return string.Format(pattern, args);
        }
        catch
        {
            return pattern;
        }
    }

    /// <summary>Choisit entre deux textes statiques selon la langue courante (compatibilité).</summary>
    public static string Pick(string fr, string en) => IsEnglish ? en : fr;

    /// <summary>Traduit une clé ou un texte statique.</summary>
    public static string Tr(string key) => Get(key);

    /// <summary>Surcharge historique (fr, en).</summary>
    public static string Tr(string fr, string en)
    {
        FrToEn[fr] = en;
        EnToFr[en] = fr;
        return IsEnglish ? en : fr;
    }

    /// <summary>Retraduit un texte déjà affiché vers la langue active.</summary>
    public static string Retranslate(string current)
    {
        if (string.IsNullOrEmpty(current)) return current;
        if (IsEnglish) return FrToEn.TryGetValue(current, out var en) ? en : current;
        return EnToFr.TryGetValue(current, out var fr) ? fr : current;
    }

    public static void LoadSettings()
    {
        try
        {
            CassiniFlow.Desktop.Services.UserSettingsService.Load();
            SetLanguage(string.Equals(CassiniFlow.Desktop.Services.UserSettingsService.Current.Language, "en", StringComparison.OrdinalIgnoreCase)
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
        Instance.SetLanguageInternal(lang, save);
    }

    public void SetLanguageInternal(AppLanguage lang, bool save = true)
    {
        if (_current == lang) return;
        _current = lang;

        if (save)
        {
            CassiniFlow.Desktop.Services.UserSettingsService.UpdateLanguage(lang == AppLanguage.English ? "en" : "fr");
        }

        // Notifier les liaisons XAML indexées
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        LanguageChanged?.Invoke();
    }

    private static void LoadLanguages()
    {
        LoadEmbeddedJson("fr.json", FrStrings);
        LoadEmbeddedJson("en.json", EnStrings);
    }

    private static void LoadEmbeddedJson(string fileName, Dictionary<string, string> targetDict)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        Stream? stream = null;
        if (resourceName != null)
        {
            stream = asm.GetManifestResourceStream(resourceName);
        }

        if (stream == null)
        {
            // Fallback disque local en développement
            var localPath = Path.Combine(AppContext.BaseDirectory, "Localization", "Languages", fileName);
            if (!File.Exists(localPath))
            {
                localPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "CassiniFlow.Desktop", "Localization", "Languages", fileName);
            }
            if (File.Exists(localPath))
            {
                stream = File.OpenRead(localPath);
            }
        }

        if (stream == null) return;

        using (stream)
        using (var doc = JsonDocument.Parse(stream))
        {
            FlattenJson(doc.RootElement, string.Empty, targetDict);
        }
    }

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string> targetDict)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var nextPrefix = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                FlattenJson(prop.Value, nextPrefix, targetDict);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            targetDict[prefix] = element.GetString() ?? string.Empty;
        }
    }

    private static void BuildDirectLookupMaps()
    {
        foreach (var (key, frVal) in FrStrings)
        {
            if (EnStrings.TryGetValue(key, out var enVal) && !string.IsNullOrEmpty(frVal))
            {
                FrToEn[frVal] = enVal;
                EnToFr[enVal] = frVal;
            }
        }
    }

    private static readonly (Regex Pattern, string Replacement)[] RuntimeRules =
    [
        (new Regex(@"^(\d+)/(\d+) frames traitées \((\d+) écrites\)\.\.\.$"), "$1/$2 frames processed ($3 written)..."),
        (new Regex(@"^(\d+)/(\d+) frames analysées \((\d+) retenues\)\.\.\.$"), "$1/$2 frames analysed ($3 kept)..."),
        (new Regex(@"^Écriture des (\d+) frames vers '(.*)'\.\.\.$"), "Writing $1 frames to '$2'..."),
        (new Regex(@"^Passe 1/2 : (\d+)/(\d+) frames analysées\.\.\.$"), "Pass 1/2: $1/$2 frames analysed..."),
        (new Regex(@"^Passe 2/2 : Écriture des (\d+) meilleures frames vers '(.*)'\.\.\.$"), "Pass 2/2: Writing the $1 best frames to '$2'..."),
        (new Regex(@"^Terminé en (\S+)s \((\S+) fps\) \S+ (\d+) frames écrites dans '(.*)'\.$"), "Finished in $1s ($2 fps) - $3 frames written to '$4'."),
        (new Regex(@"^Aucun objet isolé.*$"), "No isolated object on a black background detected - encoding all frames without rejection..."),
        (new Regex(@"^Détection de l'orientation de l'objet : (.*)\.\.\.$"), "Detecting object orientation: $1..."),
        (new Regex(@"^Rotation automatique ignorée.*$"), "Automatic rotation skipped: object not elongated enough (rings not detected)."),
        (new Regex(@"^Rotation automatique : (.*)$"), "Automatic rotation: $1"),
        (new Regex(@"^Analyse Parallèle Multi-Cœurs$"), "Multi-core parallel analysis"),
        (new Regex(@"^Traitement Final & Encodage$"), "Final processing & encoding"),
        (new Regex(@"^Passe 1 : Analyse Parallèle$"), "Pass 1: Parallel analysis"),
        (new Regex(@"^Passe 2 : Encodage$"), "Pass 2: Encoding"),
        (new Regex(@"^Terminé$"), "Done"),
        (new Regex(@"^Mode Sans Rejet$"), "No-rejection mode"),
        (new Regex(@"^Détection de l'orientation$"), "Orientation detection"),
        (new Regex(@"^Rotation auto$"), "Auto rotation"),
    ];

    public static string TranslateRuntime(string message)
    {
        if (!IsEnglish || string.IsNullOrEmpty(message)) return message;
        foreach (var (pattern, replacement) in RuntimeRules)
        {
            if (pattern.IsMatch(message)) return pattern.Replace(message, replacement);
        }
        return message;
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
        if (o is TextBox box) changed |= Apply(o, TextBox.WatermarkProperty, box.Watermark);
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
            if (current is null || !FrToEn.ContainsKey(current)) return false;
            fr = current;
            map[p] = fr;
        }

        string target = IsEnglish && FrToEn.TryGetValue(fr, out var en) ? en : fr;
        if (string.Equals(current, target, StringComparison.Ordinal)) return false;

        if (p == ToolTip.TipProperty && o is Control ctl) ToolTip.SetTip(ctl, target);
        else o.SetValue(p, target);
        return true;
    }
}

/// <summary>
/// Extension de balisage XAML permettant la syntaxe directe : Text="{loc:Tr Key}"
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay
        };
    }
}
