using Microsoft.NET.HostModel.AppHost;

if (args.Length != 3) return 64;
HostWriter.CreateAppHost(
    appHostSourceFilePath: Path.GetFullPath(args[0]),
    appHostDestinationFilePath: Path.GetFullPath(args[1]),
    appBinaryFilePath: "app/PSX.dll",
    windowsGraphicalUserInterface: true,
    assemblyToCopyResourcesFrom: Path.GetFullPath(args[2]));
return 0;
