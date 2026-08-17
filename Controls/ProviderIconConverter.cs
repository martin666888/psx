using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PSX.Controls;

public sealed class ProviderIconConverter : IValueConverter
{
    public Geometry DefaultIcon { get; set; } = Geometry.Empty;
    public Geometry ClaudeIcon { get; set; } = Geometry.Empty;
    public Geometry KimiIcon { get; set; } = Geometry.Empty;
    public Geometry QwenIcon { get; set; } = Geometry.Empty;
    public Geometry QoderIcon { get; set; } = Geometry.Empty;
    public Geometry OpencodeIcon { get; set; } = Geometry.Empty;
    public Geometry ClineIcon { get; set; } = Geometry.Empty;
    public Geometry DshIcon { get; set; } = Geometry.Empty;

    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        return Resolve(value as string) ?? DefaultIcon;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private Geometry? Resolve(string? iconKey) => iconKey?.ToLowerInvariant() switch
    {
        "claude" => ClaudeIcon,
        "kimi" => KimiIcon,
        "qwen" => QwenIcon,
        "qoder" => QoderIcon,
        "opencode" => OpencodeIcon,
        "cline" => ClineIcon,
        "dsh" => DshIcon,
        _ => null
    };
}
