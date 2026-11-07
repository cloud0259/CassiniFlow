using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Ports.Inbound;

/// <summary>
/// Port entrant (Inbound Port) orchestrant une session de Live Stacking, l'affichage temps réel et le double enregistrement (Snapshot + SER).
/// </summary>
public interface ILiveStackingUseCase : IDisposable
{
    /// <summary>
    /// Événement déclenché lorsqu'une nouvelle image empilée (avec post-traitement d'affichage) et la télémétrie associée sont prêtes.
    /// </summary>
    event Action<AstroFrame, LiveStackingTelemetry>? FrameStacked;

    /// <summary>
    /// Liste les caméras détectées par le pilote sous-jacent.
    /// </summary>
    IReadOnlyList<CameraDeviceInfo> DiscoverCameras();

    /// <summary>
    /// Connecte une caméra par son identifiant.
    /// </summary>
    bool ConnectCamera(string cameraId);

    /// <summary>
    /// Déconnecte la caméra active et arrête la session.
    /// </summary>
    void DisconnectCamera();

    bool IsConnected { get; }
    bool IsRunning { get; }
    bool IsRecordingSer { get; }
    CameraDeviceInfo? ConnectedCamera { get; }

    /// <summary>
    /// Démarre l'acquisition caméra et la boucle de Live Stacking.
    /// </summary>
    void StartSession(CameraStreamConfig streamConfig, LiveStackingOptions stackingOptions);

    /// <summary>
    /// Arrête l'acquisition caméra et la boucle de Live Stacking.
    /// </summary>
    void StopSession();

    /// <summary>
    /// Réinitialise immédiatement le buffer glissant Top-K et l'accumulateur (Reset Stack).
    /// </summary>
    void ResetStack();

    /// <summary>
    /// Met à jour à chaud les paramètres du moteur de Live Stacking (sharpening, ADC, taille du buffer, seuils) et rafraîchit l'affichage.
    /// </summary>
    void UpdateStackingOptions(LiveStackingOptions options);

    /// <summary>
    /// Met à jour à chaud les paramètres d'exposition, gain et balance des blancs de la caméra.
    /// </summary>
    void UpdateCameraControls(double exposureMs, int gain, int offset, int wbR, int wbB, bool autoExp = false, bool autoGain = false);

    /// <summary>
    /// Sauvegarde instantanément l'empilement courant en 16-bit brut (FITS/TIFF) ainsi qu'en PNG 8-bit avec accentuation.
    /// </summary>
    LiveSnapshotResult TakeSnapshot(string outputDirectory, string filePrefix = "LiveStack", bool preferFitsOverTiff = true);

    /// <summary>
    /// Démarre l'enregistrement continu du flux caméra brut vers un fichier .SER en arrière-plan.
    /// </summary>
    string StartBackgroundSerRecording(string outputDirectory, string filePrefix = "Capture");

    /// <summary>
    /// Arrête et finalise l'enregistrement du fichier .SER en arrière-plan.
    /// </summary>
    string? StopBackgroundSerRecording();
}
