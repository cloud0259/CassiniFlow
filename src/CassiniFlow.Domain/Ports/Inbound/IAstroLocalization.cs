using CassiniFlow.Domain.Localization;

namespace CassiniFlow.Domain.Ports.Inbound;

/// <summary>
/// Port Inbound du domaine pour le service universel de localisation et de traduction.
/// Définit le contrat d'inversion de dépendance (DIP) pour les cas d'utilisation,
/// adaptateurs et composants de présentation sans couplage fort.
/// </summary>
public interface IAstroLocalization
{
    DomainLanguage CurrentLanguage { get; }
    bool IsEnglish { get; }
    event Action? LanguageChanged;

    void SetLanguage(DomainLanguage lang);
    void SetLanguage(string langCode);

    string Get(string key);
    string Format(string key, params object?[] args);
    string Retranslate(string current);
    IReadOnlyDictionary<string, string> GetAll(DomainLanguage lang);
}

