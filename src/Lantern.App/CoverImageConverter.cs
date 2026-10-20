using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Lantern.App;

public sealed class CoverImageConverter : IValueConverter
{
    readonly Dictionary<string, BitmapImage> cache = new();
    readonly Queue<string> order = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        var embedded = path.StartsWith("pack://application:,,,/Assets/Maps/", StringComparison.Ordinal);
        if (!embedded && !File.Exists(path)) return null;
        var key = embedded ? path : path + File.GetLastWriteTimeUtc(path).Ticks;
        if (cache.TryGetValue(key, out var found)) return found;
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            while (order.Count >= 96) cache.Remove(order.Dequeue());
            cache[key] = image; order.Enqueue(key); return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException) { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
