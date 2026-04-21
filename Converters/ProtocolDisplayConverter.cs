using System.Globalization;
using System.Windows.Data;
using StreamCaster.Models;

namespace StreamCaster.Converters;

[ValueConversion(typeof(StreamProtocol), typeof(string))]
public sealed class ProtocolDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is StreamProtocol p ? p switch
        {
            StreamProtocol.Udp => "UDP",
            StreamProtocol.Rtsp => "RTSP",
            StreamProtocol.Http => "HTTP",
            _ => value.ToString() ?? string.Empty,
        } : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
