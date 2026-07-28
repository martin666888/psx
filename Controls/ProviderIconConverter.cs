using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PSX.Controls;

public sealed class ProviderIconConverter : IValueConverter
{
    public Geometry DefaultIcon { get; set; } = Geometry.Empty;
    public Geometry ClaudeIcon { get; set; } = Geometry.Empty;
    public Geometry KimiIcon { get; set; } = Geometry.Empty;

    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        return (value as string) switch
        {
            string key when key.Equals("claude", StringComparison.OrdinalIgnoreCase) => ClaudeIcon,
            string key when key.Equals("kimi", StringComparison.OrdinalIgnoreCase) => KimiIcon,
            _ => DefaultIcon
        };
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
