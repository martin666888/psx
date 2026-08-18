using System.IO;

namespace PSX.Services;

/// <summary>
/// Shared current/next/pointer directory mechanics for managed runtimes.
/// Provider validation, seeding, version policy, and executable selection stay
/// in each runtime implementation.
/// </summary>
internal sealed class StagedRuntimeStore
{
    private readonly string _productName;
    private readonly Action<string> _log;

    public StagedRuntimeStore(string productName, Action<string> log)
    {
        _productName = productName;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool PointerSaysNext(string pointerFile, string nextToken = "next")
    {
        try
        {
            return File.Exists(pointerFile)
                && string.Equals(File.ReadAllText(pointerFile).Trim(), nextToken, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _log($"WARN: could not read {_productName} active pointer: {ex.Message}");
            return false;
        }
    }

    public bool TryWriteActivePointer(
        string runtimeRoot,
        string pointerFile,
        string token)
    {
        try
        {
            Directory.CreateDirectory(runtimeRoot);
            var temporary = pointerFile + ".tmp";
            File.WriteAllText(temporary, token);
            File.Move(temporary, pointerFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Failed to write {_productName} active pointer '{token}': {ex}");
            return false;
        }
    }

    public bool PromoteNextToCurrent(
        string runtimeRoot,
        string currentDirectory,
        string nextDirectory,
        string pointerFile,
        string currentToken = "current")
    {
        var backup = currentDirectory + ".old";
        try
        {
            DeleteIfPresent(backup);
            if (Directory.Exists(currentDirectory))
                Directory.Move(currentDirectory, backup);
            Directory.Move(nextDirectory, currentDirectory);
            DeleteIfPresent(backup);
        }
        catch (Exception ex)
        {
            _log($"{_productName} promote failed mid-swap: {ex}. Recovering.");
            if (Directory.Exists(backup) && !Directory.Exists(currentDirectory))
            {
                try { Directory.Move(backup, currentDirectory); }
                catch (Exception recoveryError)
                {
                    _log($"WARN: {_productName} rollback failed: {recoveryError.Message}");
                }
            }
            TryWriteActivePointer(runtimeRoot, pointerFile, currentToken);
            return false;
        }

        TryWriteActivePointer(runtimeRoot, pointerFile, currentToken);
        _log($"{_productName} promote: next is now current.");
        return true;
    }

    public void ClearStaleNext(string nextDirectory)
    {
        try
        {
            DeleteIfPresent(nextDirectory);
        }
        catch (Exception ex)
        {
            _log($"WARN: could not clear stale {_productName} next directory: {ex.Message}");
        }
    }

    public void DropRuntimeCopies(params string[] directories)
    {
        foreach (var directory in directories)
        {
            try
            {
                DeleteIfPresent(directory);
            }
            catch (Exception ex)
            {
                _log($"WARN: could not drop {directory}: {ex.Message}");
            }
        }
    }

    private static void DeleteIfPresent(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
