namespace PSX.Models;

public enum ThemeSource
{
    BuiltIn,
    User
}

public enum ThemeAvailability
{
    Available,
    Invalid
}

public sealed class ThemeDiagnostic
{
    public bool IsError { get; init; }
    public string Message { get; init; } = "";
}

public sealed class ThemeDescriptor
{
    public string Key { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public ThemeSource Source { get; init; }
    public ThemeAvailability Availability { get; set; }
    public List<ThemeDiagnostic> Diagnostics { get; } = [];
    public AppearanceSettings? Appearance { get; init; }

    public string DiagnosticSummary => string.Join(Environment.NewLine, Diagnostics.Select(d => d.Message));
}

public sealed class ThemeValidationResult
{
    public ThemeDescriptor Descriptor { get; init; } = new();
    public bool IsValid => Descriptor.Availability == ThemeAvailability.Available;
}
