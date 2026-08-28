using System.Globalization;
using System.Windows.Data;

namespace GeminiLiveShare.App.Converters;

public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && parameter is string scaleText &&
            double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            return Math.Max(0, width * scale);
        }

        return 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}