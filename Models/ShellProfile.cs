namespace PSX.Models;

public sealed class ShellProfile
{
    public string Id { get; set; } = "cmd";
    public string Name { get; set; } = "Command Prompt";
    public string Command { get; set; } = "cmd.exe";
    public string Arguments { get; set; } = "";
    public string? StartingDirectory { get; set; }
    public bool IsDefault { get; set; }

    public static ShellProfile Cmd => new()
    {
        Id = "cmd",
        Name = "Command Prompt",
        Command = "cmd.exe",
        IsDefault = true
    };

    public static ShellProfile PowerShell => new()
    {
        Id = "powershell",
        Name = "Windows PowerShell",
        Command = "powershell.exe",
        IsDefault = false
    };
}
