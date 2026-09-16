param(
    [Parameter(Mandatory=$true)][string]$PortableZip,
    [Parameter(Mandatory=$true)][string]$OutputZip
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$compactStage = Join-Path $repoRoot ('TestResults\compact-build\' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $compactStage 'app'
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($PortableZip), $appDirectory)
Move-Item -LiteralPath (Join-Path $appDirectory 'psx.ini') -Destination $compactStage
# Build a native apphost using exactly the SDK selected by global.json.
$sdkVersion = (& dotnet --version).Trim()
$sdkLine = & dotnet --list-sdks | Where-Object { $_ -like "$sdkVersion *" } | Select-Object -First 1
if ($sdkLine -notmatch '\[(.+)\]') { throw 'Could not locate selected SDK' }
$dotnetRoot = Split-Path -Parent $Matches[1]
$runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $appDirectory 'PSX.runtimeconfig.json') | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$template = Join-Path $dotnetRoot "packs\Microsoft.NETCore.App.Host.win-x64\$runtimeVersion\runtimes\win-x64\native\apphost.exe"
if (-not (Test-Path -LiteralPath $template)) { throw "Missing matching apphost template: $template" }
& dotnet run --project (Join-Path $PSScriptRoot 'PSX.PackageBuilder\PSX.PackageBuilder.csproj') -c Release -- $template (Join-Path $compactStage 'PSX.exe') (Join-Path $appDirectory 'PSX.dll')
if ($LASTEXITCODE -ne 0) { throw 'Compact apphost generation failed' }
[IO.File]::WriteAllText((Join-Path $appDirectory 'psx-compact-layout.json'), '{"version":1}')
# The public entry is the root apphost; avoid a misleading second launch entry.
Remove-Item -LiteralPath (Join-Path $appDirectory 'PSX.exe') -Force
$fullOutput = [IO.Path]::GetFullPath($OutputZip)
if (Test-Path -LiteralPath $fullOutput) { Remove-Item -LiteralPath $fullOutput -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($compactStage, $fullOutput, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($fullOutput)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    if ($names -notcontains 'PSX.exe' -or $names -notcontains 'psx.ini' -or $names -notcontains 'app/PSX.dll') { throw 'Incomplete compact package' }
    if (@($names | Where-Object { $_ -ne 'PSX.exe' -and $_ -ne 'psx.ini' -and -not $_.StartsWith('app/') }).Count) { throw 'Unexpected compact root entry' }
} finally { $archive.Dispose() }
Write-Host "Compact ZIP: $fullOutput"
Write-Host "Compact SHA-256: $((Get-FileHash -LiteralPath $fullOutput -Algorithm SHA256).Hash)"
Write-Host "Compact staging: $compactStage"
