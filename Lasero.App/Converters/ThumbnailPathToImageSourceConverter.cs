using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Lasero.App.Converters;

/// <summary>Loads a cached project thumbnail PNG for display, or null (letting the fallback icon
/// underneath show through) when there isn't one yet or the file went missing/corrupt.</summary>
public sealed class ThumbnailPathToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
