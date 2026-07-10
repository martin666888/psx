using System.IO;
using Microsoft.Win32;

namespace PSX.Services;

public sealed class WpfAgentDirectoryPicker : IAgentDirectoryPicker
{
    public string? PickDirectory(string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Claude Code working directory",
            InitialDirectory = Directory.Exists(initialDirectory)
                ? initialDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
