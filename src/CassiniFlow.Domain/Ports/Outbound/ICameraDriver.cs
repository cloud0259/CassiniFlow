using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Ports.Outbound;

/// <summary>
/// Port sortant (Outbound Port) pour le pilotage matériel d'une caméra astronomique haute cadence (USB3 / SDK / Simulateur).
/// </summary>
public interface ICameraDriver : IDisposable
{
    /// <summary>
    /// Énumère les caméras astronomiques disponibles (physiques et/ou simulées).
    /// </summary>
    IReadOnlyList<CameraDeviceInfo> DiscoverCameras();

    /// <summary>
    /// Ouvre et initialise la connexion avec la caméra identifiée par son ID.
    /// </summary>
    bool Connect(string cameraId);

    /// <summary>
    /// Ferme proprement la connexion caméra et arrête tout flux actif.
    /// </summary>
    void Disconnect();

    /// <summary>
    /// Indique si une caméra est actuellement connectée.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Informations sur la caméra actuellement connectée.
    /// </summary>
    CameraDeviceInfo? ConnectedCamera { get; }

    /// <summary>
    /// Indique si l'acquisition vidéo en continu est active.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Démarre le flux d'acquisition vidéo continu et invoque le callback à chaque trame reçue.
    /// </summary>
    void StartVideoStream(CameraStreamConfig config, Action<AstroFrame> onFrameCaptured);

    /// <summary>
    /// Arrête le flux d'acquisition vidéo continu.
    /// </summary>
    void StopVideoStream();

    /// <summary>
    /// Met à jour à chaud le temps d'exposition (ms), le gain et l'offset de la caméra.
    /// </summary>
    void UpdateExposureAndGain(double exposureMs, int gain, int offset = 10, bool autoExposure = false, bool autoGain = false);

    /// <summary>
    /// Met à jour à chaud la balance des blancs matérielle (canaux Rouge et Bleu).
    /// </summary>
    void UpdateWhiteBalance(int wbR, int wbB);

    /// <summary>
    /// Lit la température actuelle du capteur CMOS en degrés Celsius.
    /// </summary>
    double GetSensorTemperatureCelsius();

    /// <summary>
    /// Retourne le nombre de trames perdues par le contrôleur USB depuis le début du flux.
    /// </summary>
    int GetDroppedFramesCount();
}
