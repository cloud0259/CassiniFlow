using System;
using System.Collections.Generic;
using System.IO;
using CassiniFlow.Domain.Exceptions;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Domain.Services;

public static class CassiniFlowErrorExplainer
{
    public static CassiniFlowErrorDetails Explain(Exception ex, IAstroLocalization? loc = null)
    {
        string GetText(string key, string fallback)
        {
            if (loc == null) return fallback;
            string val = loc.Get(key);
            return string.IsNullOrEmpty(val) || val == key ? fallback : val;
        }

        Exception actual = ex;
        if (actual is AggregateException ae)
        {
            actual = ae.Flatten().InnerException ?? actual;
        }

        string techDetails = BuildTechnicalDetails(ex);

        if (actual is CassiniFlowException cfe)
        {
            return new CassiniFlowErrorDetails(
                cfe.ErrorCode,
                cfe.UserTitle,
                cfe.UserExplanation,
                cfe.SuggestedResolution,
                techDetails);
        }

        if (actual is OutOfMemoryException)
        {
            return new CassiniFlowErrorDetails(
                "ERR_OUT_OF_MEMORY",
                GetText("Errors.TitleOutOfMemory", "Mémoire vive (RAM) insuffisante"),
                GetText("Errors.ExplanationOutOfMemory", "Le traitement requiert plus de mémoire vive que la quantité actuellement disponible sur le système."),
                GetText("Errors.SuggestionOutOfMemory", "• Activez le recadrage (Crop) pour limiter les dimensions de traitement à la zone de la planète.\n• Réduisez le plafond CPU dans les Préférences afin d'allouer moins de threads simultanés.\n• Fermez les autres logiciels volumineux en cours d'exécution."),
                techDetails);
        }

        if (actual is FileNotFoundException fnf)
        {
            string file = Path.GetFileName(fnf.FileName ?? fnf.Message);
            return new CassiniFlowErrorDetails(
                "ERR_FILE_NOT_FOUND",
                GetText("Errors.TitleCorruptMedia", "Fichier introuvable ou inaccessible"),
                string.Format(GetText("Errors.ExplanationCorruptMedia", "Le fichier '{0}' est introuvable ou a été déplacé."), file),
                GetText("Errors.SuggestionCorruptMedia", "• Vérifiez l'intégrité du fichier sur votre disque et assurez-vous que le support de stockage n'a pas été déconnecté."),
                techDetails);
        }

        if (actual is DirectoryNotFoundException)
        {
            return new CassiniFlowErrorDetails(
                "ERR_OUTPUT_DIRECTORY",
                GetText("Errors.TitleOutputDirectory", "Dossier de sortie inaccessible"),
                GetText("Errors.ExplanationOutputDirectory", "Impossible de créer ou d'écrire les fichiers dans le dossier de destination spécifié."),
                GetText("Errors.SuggestionOutputDirectory", "• Vérifiez que le dossier de destination existe et n'est pas en lecture seule.\n• Assurez-vous de disposer de l'espace disque suffisant pour enregistrer les fichiers."),
                techDetails);
        }

        if (actual is UnauthorizedAccessException)
        {
            return new CassiniFlowErrorDetails(
                "ERR_PERMISSION_DENIED",
                GetText("Errors.TitleOutputDirectory", "Accès refusé"),
                GetText("Errors.ExplanationOutputDirectory", "L'application ne dispose pas des droits requis pour lire ou écrire ce fichier."),
                GetText("Errors.SuggestionOutputDirectory", "• Vérifiez les permissions d'accès au dossier de sortie ou lancez l'application avec les privilèges appropriés."),
                techDetails);
        }

        string msg = actual.Message ?? string.Empty;
        string typeName = actual.GetType().FullName ?? string.Empty;

        if (msg.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase) || msg.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return new CassiniFlowErrorDetails(
                "ERR_CODEC",
                GetText("Errors.TitleCodec", "Erreur d'encodage ou de décodage vidéo (FFmpeg)"),
                GetText("Errors.ExplanationCodec", "Le codec vidéo requis ou l'exécutable FFmpeg n'a pas pu traiter le flux vidéo."),
                GetText("Errors.SuggestionCodec", "• Vérifiez que FFmpeg est installé et accessible dans votre variable d'environnement PATH.\n• Essayez de choisir un autre format de sortie (ex: SER ou AVI non compressé)."),
                techDetails);
        }

        if (actual is NotSupportedException || msg.Contains("non supporté", StringComparison.OrdinalIgnoreCase) || msg.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return new CassiniFlowErrorDetails(
                "ERR_UNSUPPORTED_FORMAT",
                GetText("Errors.TitleUnsupportedFormat", "Format de fichier non supporté"),
                string.IsNullOrWhiteSpace(msg)
                    ? GetText("Errors.ExplanationUnsupportedFormat", "Le fichier sélectionné ne correspond à aucun format astronomique ou vidéo reconnu.")
                    : msg,
                GetText("Errors.SuggestionUnsupportedFormat", "CassiniFlow prend en charge les formats SER (.ser), FITS (.fits, .fit) ainsi que les conteneurs vidéo AVI, MP4, MOV, MKV et les séquences d'images (TIFF, PNG, BMP)."),
                techDetails);
        }

        if (actual is InvalidDataException || msg.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || msg.Contains("signature", StringComparison.OrdinalIgnoreCase) || msg.Contains("header", StringComparison.OrdinalIgnoreCase))
        {
            return new CassiniFlowErrorDetails(
                "ERR_CORRUPT_MEDIA",
                GetText("Errors.TitleCorruptMedia", "Fichier corrompu ou incomplet"),
                string.IsNullOrWhiteSpace(msg)
                    ? GetText("Errors.ExplanationCorruptMedia", "L'en-tête ou le flux de trames du fichier est endommagé ou tronqué.")
                    : msg,
                GetText("Errors.SuggestionCorruptMedia", "• Vérifiez l'intégrité du fichier sur votre disque.\n• Si l'enregistrement a été interrompu brusquement, réparez le conteneur ou ré-exportez depuis le logiciel de capture."),
                techDetails);
        }

        if (typeName.Contains("CvException", StringComparison.OrdinalIgnoreCase) || typeName.Contains("OpenCv", StringComparison.OrdinalIgnoreCase) || msg.Contains("OpenCV", StringComparison.OrdinalIgnoreCase))
        {
            return new CassiniFlowErrorDetails(
                "ERR_OPENCV",
                GetText("Errors.TitleOpenCv", "Erreur de traitement d'image (OpenCV)"),
                GetText("Errors.ExplanationOpenCv", "Une erreur de calcul matriciel ou de débayérisation est survenue."),
                GetText("Errors.SuggestionOpenCv", "Vérifiez que la matrice de Bayer sélectionnée correspond à votre caméra et que les dimensions de recadrage sont paires."),
                techDetails);
        }

        return new CassiniFlowErrorDetails(
            "ERR_GENERIC",
            GetText("Errors.TitleGeneric", "Une erreur est survenue"),
            string.IsNullOrWhiteSpace(msg) ? actual.GetType().Name : msg,
            GetText("Errors.SuggestionGeneric", "Consultez les détails techniques ci-dessous ou vérifiez les paramètres de la séquence."),
            techDetails);
    }

    private static string BuildTechnicalDetails(Exception ex)
    {
        var lines = new List<string>
        {
            $"{ex.GetType().FullName}: {ex.Message}"
        };

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            lines.Add(ex.StackTrace);
        }

        Exception? inner = ex.InnerException;
        while (inner != null)
        {
            lines.Add($"--- Inner Exception: {inner.GetType().FullName}: {inner.Message}");
            if (!string.IsNullOrWhiteSpace(inner.StackTrace))
            {
                lines.Add(inner.StackTrace);
            }
            inner = inner.InnerException;
        }

        return string.Join(Environment.NewLine, lines);
    }
}
