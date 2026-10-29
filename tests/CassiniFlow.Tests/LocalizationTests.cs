using System.Text.Json;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class LocalizationTests
{
    private static void FlattenKeys(JsonElement element, string prefix, HashSet<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var nextPrefix = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                FlattenKeys(prop.Value, nextPrefix, keys);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            keys.Add(prefix);
        }
    }

    [Fact]
    public void LocalizationJsonFiles_ExistAndHaveIdenticalKeys()
    {
        var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CassiniFlow.Desktop", "Localization", "Languages"));
        var frPath = Path.Combine(basePath, "fr.json");
        var enPath = Path.Combine(basePath, "en.json");

        Assert.True(File.Exists(frPath), $"fr.json not found at {frPath}");
        Assert.True(File.Exists(enPath), $"en.json not found at {enPath}");

        using var frDoc = JsonDocument.Parse(File.ReadAllText(frPath));
        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));

        var frKeys = new HashSet<string>();
        var enKeys = new HashSet<string>();

        FlattenKeys(frDoc.RootElement, string.Empty, frKeys);
        FlattenKeys(enDoc.RootElement, string.Empty, enKeys);

        Assert.NotEmpty(frKeys);
        Assert.NotEmpty(enKeys);

        var missingInEn = frKeys.Except(enKeys).ToList();
        var missingInFr = enKeys.Except(frKeys).ToList();

        Assert.True(missingInEn.Count == 0, $"Keys missing in en.json: {string.Join(", ", missingInEn)}");
        Assert.True(missingInFr.Count == 0, $"Keys missing in fr.json: {string.Join(", ", missingInFr)}");
    }

    [Fact]
    public void Loc_CanRetrieveFrenchAndEnglishStrings()
    {
        Loc.SetLanguage(AppLanguage.French, save: false);
        Assert.False(Loc.IsEnglish);
        Assert.Equal("Quitter", Loc.Get("Menu.Exit"));
        Assert.Contains("Aucun fichier vidéo chargé", Loc.Get("Viewer.NoVideoPrompt"));

        Loc.SetLanguage(AppLanguage.English, save: false);
        Assert.True(Loc.IsEnglish);
        Assert.Equal("Exit", Loc.Get("Menu.Exit"));
        Assert.Contains("No video file loaded", Loc.Get("Viewer.NoVideoPrompt"));

        // Reset to French
        Loc.SetLanguage(AppLanguage.French, save: false);
    }

    [Fact]
    public void Loc_Format_SubstitutesArguments()
    {
        Loc.SetLanguage(AppLanguage.French, save: false);
        var formattedFr = Loc.Format("Messages.ObjectSize", 42);
        Assert.Contains("42 px", formattedFr);

        Loc.SetLanguage(AppLanguage.English, save: false);
        var formattedEn = Loc.Format("Messages.ObjectSize", 42);
        Assert.Contains("42 px", formattedEn);

        Loc.SetLanguage(AppLanguage.French, save: false);
    }

    [Fact]
    public void Loc_TranslateRuntime_TranslatesEngineLogMessages()
    {
        Loc.SetLanguage(AppLanguage.English, save: false);

        var translated = Loc.TranslateRuntime("10/20 frames traitées (10 écrites)...");
        Assert.Equal("10/20 frames processed (10 written)...", translated);

        Loc.SetLanguage(AppLanguage.French, save: false);
        var unchanged = Loc.TranslateRuntime("10/20 frames traitées (10 écrites)...");
        Assert.Equal("10/20 frames traitées (10 écrites)...", unchanged);
    }

    [Fact]
    public void Tabs_1To9_HaveFrenchAndEnglishTranslations()
    {
        Loc.SetLanguage(AppLanguage.French, save: false);
        Assert.Equal("Fichiers sources", Loc.Get("Tab.SourceFiles"));
        Assert.Equal("Options d'entrée", Loc.Get("Tab.InputOptions"));
        Assert.Equal("Traitement & Centrage", Loc.Get("Tab.ProcessingOptions"));
        Assert.Equal("Tri & Qualité", Loc.Get("Tab.QualityOptions"));
        Assert.Equal("Points d'alignement", Loc.Get("Tab.AlignmentOptions"));
        Assert.Equal("Empilement & Rendu", Loc.Get("Tab.StackingOptions"));
        Assert.Equal("Animation", Loc.Get("Tab.AnimationOptions"));
        Assert.Equal("Options de sortie", Loc.Get("Tab.OutputOptions"));
        Assert.Equal("Exécution", Loc.Get("Tab.DoProcessing"));

        Loc.SetLanguage(AppLanguage.English, save: false);
        Assert.Equal("Source Files", Loc.Get("Tab.SourceFiles"));
        Assert.Equal("Input Options", Loc.Get("Tab.InputOptions"));
        Assert.Equal("Processing Options", Loc.Get("Tab.ProcessingOptions"));
        Assert.Equal("Quality Options", Loc.Get("Tab.QualityOptions"));
        Assert.Equal("Alignment Points", Loc.Get("Tab.AlignmentOptions"));
        Assert.Equal("Stacking & Render", Loc.Get("Tab.StackingOptions"));
        Assert.Equal("Animation", Loc.Get("Tab.AnimationOptions"));
        Assert.Equal("Output Options", Loc.Get("Tab.OutputOptions"));
        Assert.Equal("Execution", Loc.Get("Tab.DoProcessing"));

        Loc.SetLanguage(AppLanguage.French, save: false);
    }
}
