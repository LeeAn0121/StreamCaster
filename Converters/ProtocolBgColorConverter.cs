using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using StreamCaster.Models;

namespace StreamCaster.Converters;

[ValueConversion(typeof(StreamProtocol), typeof(Brush))]
public sealed class ProtocolBgColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is StreamProtocol p ? p switch
        {
            StreamProtocol.Udp => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),
            StreamProtocol.Rtsp => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),
            StreamProtocol.Http => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),
            _ => Brushes.Gray,
        } : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
