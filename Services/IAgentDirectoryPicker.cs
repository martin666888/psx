namespace PSX.Services;

public interface IAgentDirectoryPicker
{
    string? PickDirectory(string initialDirectory);
}
