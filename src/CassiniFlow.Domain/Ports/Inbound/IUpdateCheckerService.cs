using System.Threading;
using System.Threading.Tasks;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Ports.Inbound;

/// <summary>
/// Port inbound permettant de vérifier la disponibilité d'une nouvelle version de l'application.
/// </summary>
public interface IUpdateCheckerService
{
    /// <summary>
    /// Version courante de l'application (ex: "1.1.0").
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Interroge le dépôt officiel pour détecter si une version supérieure est disponible.
    /// </summary>
    Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default);
}
