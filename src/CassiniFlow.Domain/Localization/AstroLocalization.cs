using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Domain.Localization;

/// <summary>
/// Implémentation du service de domaine pour le port <see cref="IAstroLocalization"/>.
/// Charge les dictionnaires JSON intégrés (fr.json, en.json) et gère la traduction métier universelle.
/// Peut être instancié de manière isolée pour l'injection de dépendances (DI) et les tests unitaires.
/// </summary>
public class AstroLocalizationService : IAstroLocalization
{
    private static readonly Dictionary<string, string> SharedFrStrings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SharedEnStrings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SharedFrToEn = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> SharedEnToFr = new(StringComparer.Ordinal);
    private static readonly CultureInfo FrCulture = CultureInfo.GetCultureInfo("fr-FR");

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

    static AstroLocalizationService()
    {
        LoadLanguages(SharedFrStrings, SharedEnStrings);
        BuildDirectLookupMaps(SharedFrStrings, SharedEnStrings, SharedFrToEn, SharedEnToFr);
    }

    private DomainLanguage _current = DomainLanguage.French;

    public DomainLanguage CurrentLanguage => _current;
    public bool IsEnglish => _current == DomainLanguage.English;

    public event Action? LanguageChanged;

    public void SetLanguage(DomainLanguage lang)
    {
        if (_current == lang) return;
        _current = lang;
        LanguageChanged?.Invoke();
    }

    public void SetLanguage(string langCode)
    {
        var lang = string.Equals(langCode, "en", StringComparison.OrdinalIgnoreCase)
            ? DomainLanguage.English
            : DomainLanguage.French;
        SetLanguage(lang);
    }

    public string Get(string key)
    {
        if (string.Equals(key, "About.Version", StringComparison.OrdinalIgnoreCase))
        {
            var asm = typeof(AstroLocalizationService).Assembly;
            var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                int plusIdx = infoVer.IndexOf('+');
                if (plusIdx > 0) infoVer = infoVer[..plusIdx];
                return $"Version {infoVer.TrimStart('v', 'V')}";
            }
            var ver = asm.GetName().Version;
            return $"Version {ver?.Major ?? 1}.{ver?.Minor ?? 1}.{ver?.Build ?? 0}";
        }

        var isEng = _current == DomainLanguage.English;
        var dict = isEng ? SharedEnStrings : SharedFrStrings;
        if (dict.TryGetValue(key, out var val)) return val;

        // Fallback vers le dictionnaire français si clé absente en anglais
        if (isEng && SharedFrStrings.TryGetValue(key, out var frVal)) return frVal;

        // Fallback direct sur la clé elle-même si absente
        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string pattern = Get(key);
        try
        {
            var culture = _current == DomainLanguage.English ? CultureInfo.InvariantCulture : FrCulture;
            return string.Format(culture, pattern, args);
        }
        catch
        {
            return pattern;
        }
    }

    public string Retranslate(string current)
    {
        if (string.IsNullOrEmpty(current)) return current;
        if (_current == DomainLanguage.English)
        {
            if (SharedFrToEn.TryGetValue(current, out var en)) return en;
            foreach (var (pattern, replacement) in RuntimeRules)
            {
                if (pattern.IsMatch(current)) return pattern.Replace(current, replacement);
            }
            return current;
        }
        else
        {
            return SharedEnToFr.TryGetValue(current, out var fr) ? fr : current;
        }
    }

    public IReadOnlyDictionary<string, string> GetAll(DomainLanguage lang) =>
        lang == DomainLanguage.English ? SharedEnStrings : SharedFrStrings;

    private static void LoadLanguages(Dictionary<string, string> frDict, Dictionary<string, string> enDict)
    {
        LoadEmbeddedJson("fr.json", frDict);
        LoadEmbeddedJson("en.json", enDict);
    }

    private static void LoadEmbeddedJson(string fileName, Dictionary<string, string> targetDict)
    {
        var asm = typeof(AstroLocalizationService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null) return;

        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        FlattenJson(doc.RootElement, string.Empty, targetDict);
    }

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string> target)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                string key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                FlattenJson(prop.Value, key, target);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            target[prefix] = element.GetString() ?? string.Empty;
        }
    }

    private static void BuildDirectLookupMaps(
        Dictionary<string, string> fr,
        Dictionary<string, string> en,
        Dictionary<string, string> frToEn,
        Dictionary<string, string> enToFr)
    {
        foreach (var (key, frVal) in fr)
        {
            if (en.TryGetValue(key, out var enVal) && !string.IsNullOrEmpty(frVal))
            {
                frToEn[frVal] = enVal;
                enToFr[enVal] = frVal;
            }
        }
    }
}

