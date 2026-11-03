using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace CassiniFlow.Desktop.Converters;

/// <summary>
/// Formate la taille d'un fichier en octets / Ko / Mo à partir de son chemin d'accès.
/// </summary>
public sealed class FileSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && File.Exists(path))
        {
            try
            {
                var fi = new FileInfo(path);
                long bytes = fi.Length;
                if (bytes < 1024) return Localization.Loc.Format("UI.FileSizeBytes", bytes);
                if (bytes < 1024 * 1024) return Localization.Loc.Format("UI.FileSizeKb", bytes / 1024.0);
                return Localization.Loc.Format("UI.FileSizeMb", bytes / (1024.0 * 1024.0));
            }
            catch
            {
                return string.Empty;
            }
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
