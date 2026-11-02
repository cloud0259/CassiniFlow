using System;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Moteur de dérotation de champ pour télescopes Dobson et montures Alt-Azimutales.
/// Basé sur les lois de mécanique céleste et la feuille de calcul astronomique rotation-champ_dobson.xlsx.
/// </summary>
public static class FieldDerotationEngine
{
    /// <summary>Vitesse de rotation sidérale terrestre en degrés par seconde (360° / 86164.0905 s).</summary>
    public const double EarthSiderealRateDegPerSec = 360.0 / 86164.0905; // ~0.00417807 °/s

    /// <summary>Constante en minutes (~1 rad / 0.004363 rad/min ~ 230 min) issue du tableau de calcul rotation-champ_dobson.xlsx.</summary>
    public const double DobsonConstantMinutes = 230.0;

    /// <summary>
    /// Calcule le taux instantané de rotation de champ (en degrés par seconde) pour une monture alt-azimutale.
    /// w = w_sideral * (cos(Latitude) * cos(Azimuth)) / cos(Altitude).
    /// Convention d'azimut astronomique standard : Sud = 180°.
    /// </summary>
    public static double ComputeFieldRotationRateDegPerSec(double latitudeDeg, double altitudeDeg, double azimuthDeg)
    {
        double latRad = latitudeDeg * Math.PI / 180.0;
        double altRad = Math.Clamp(altitudeDeg, -89.9, 89.9) * Math.PI / 180.0;
        double azRad = azimuthDeg * Math.PI / 180.0;

        double cosAlt = Math.Max(0.01, Math.Cos(altRad));
        return EarthSiderealRateDegPerSec * (Math.Cos(latRad) * Math.Cos(azRad)) / cosAlt;
    }

    /// <summary>
    /// Calcule le taux instantané de rotation de champ en degrés par minute.
    /// </summary>
    public static double ComputeFieldRotationRateDegPerMin(double latitudeDeg, double altitudeDeg, double azimuthDeg) =>
        ComputeFieldRotationRateDegPerSec(latitudeDeg, altitudeDeg, azimuthDeg) * 60.0;

    /// <summary>
    /// Calcule le temps maximal d'exposition (en secondes) avant qu'un pixel situé au bord du capteur
    /// (à distance r = sensorWidth / 2) ne subisse un déplacement supérieur au bougé admissible.
    /// Formule exacte de rotation-champ_dobson.xlsx :
    /// T_minutes = 230 * ((B * cos(H)) / (d * cos(L) * cos(A)))
    /// T_secondes = |T_minutes * 60|
    /// </summary>
    public static double ComputeMaxExposureSeconds(
        double latitudeDeg,
        double altitudeDeg,
        double azimuthDeg,
        double sensorRadiusPixels,
        double admissibleBlurPixels = 1.5)
    {
        if (sensorRadiusPixels <= 0) return 0.0;

        double latRad = latitudeDeg * Math.PI / 180.0;
        double altRad = Math.Clamp(altitudeDeg, -89.9, 89.9) * Math.PI / 180.0;
        double azRad = azimuthDeg * Math.PI / 180.0;

        double cosH = Math.Cos(altRad);
        double cosL = Math.Cos(latRad);
        double cosA = Math.Cos(azRad);

        double denom = sensorRadiusPixels * cosL * cosA;
        if (Math.Abs(denom) < 1e-7) return 99999.0;

        double tMinutes = DobsonConstantMinutes * ((admissibleBlurPixels * cosH) / denom);
        return Math.Abs(tMinutes * 60.0);
    }

    /// <summary>
    /// Calcule l'angle de dérotation (en degrés) à appliquer à une frame capturée à elapsedSeconds
    /// par rapport au temps médian de référence referenceSeconds.
    /// </summary>
    public static double ComputeDerotationAngle(
        double elapsedSeconds,
        double referenceSeconds,
        double latitudeDeg,
        double altitudeDeg,
        double azimuthDeg)
    {
        double rate = ComputeFieldRotationRateDegPerSec(latitudeDeg, altitudeDeg, azimuthDeg);
        return -rate * (elapsedSeconds - referenceSeconds);
    }

    /// <summary>
    /// Calcule l'angle de dérotation (en degrés) pour une frame donnée au sein d'une séquence vidéo.
    /// </summary>
    public static double ComputeFrameDerotationAngle(
        int frameIndex,
        int totalFrames,
        double fps,
        double latitudeDeg,
        double altitudeDeg,
        double azimuthDeg)
    {
        double safeFps = fps > 0 ? fps : 30.0;
        int safeTotal = Math.Max(1, totalFrames);
        double elapsedSec = frameIndex / safeFps;
        double refSec = (safeTotal / 2.0) / safeFps;
        return ComputeDerotationAngle(elapsedSec, refSec, latitudeDeg, altitudeDeg, azimuthDeg);
    }

    /// <summary>
    /// Applique la dérotation sur une trame en la faisant pivoter autour de son centre géométrique.
    /// </summary>
    public static AstroFrame ApplyDerotation(AstroFrame frame, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < 0.01) return frame;
        return AutoRotationAligner.Rotate(frame, angleDegrees);
    }
}
