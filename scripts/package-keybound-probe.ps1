param(
    [string]$OutputDirectory,
    [string]$LicensePath,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceRoot 'release'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactDirectory = Join-Path $workspaceRoot 'src\KeyboundProbe\artifacts'
$artifact = Join-Path $artifactDirectory 'KeyboundProbe.ActPlugin.dll'
$wordTable = Join-Path $artifactDirectory 'KeyboundProbe.words.tsv'
$packaging = Join-Path $workspaceRoot 'packaging\KeyboundProbe'
$versionPath = Join-Path $workspaceRoot 'VERSION'
if ([string]::IsNullOrWhiteSpace($LicensePath)) {
    $LicensePath = Join-Path $workspaceRoot 'LICENSE'
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-keybound-probe.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'KeyboundProbe build failed.' }
}
if (-not $SkipTests) {
    & (Join-Path $artifactDirectory 'KeyboundProbe.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'KeyboundProbe tests failed.' }
}
foreach ($required in @($artifact, $wordTable, $packaging, $versionPath, $LicensePath,
        (Join-Path $workspaceRoot 'src\KeyboundProbe\overlay\index.html'),
        (Join-Path $workspaceRoot 'src\KeyboundProbe\overlay\overlay.css'),
        (Join-Path $workspaceRoot 'src\KeyboundProbe\overlay\overlay.js'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required package input missing: $required" }
}

$version = [IO.File]::ReadAllText($versionPath).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid VERSION: $version" }
$licenseText = [IO.File]::ReadAllText($LicensePath)
if ($licenseText.Contains('[Copyright Holder]')) {
    throw 'LICENSE still contains [Copyright Holder]. Fill in the real copyright holder before packaging.'
}
$builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($artifact).ProductVersion
if ($builtVersion -ne $version) {
    throw "Built DLL version $builtVersion does not match VERSION $version. Rebuild first."
}
$packageName = "MoguangTyping-$version"
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = Join-Path $OutputDirectory $packageName
$zip = Join-Path $OutputDirectory "$packageName.zip"
$zipChecksum = "$zip.sha256"

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
$resolvedStage = [IO.Path]::GetFullPath($stage)
if (-not $resolvedStage.StartsWith($resolvedOutput, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedStage) -ne $packageName) {
    throw "Unsafe package staging path: $resolvedStage"
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
if (Test-Path -LiteralPath $zipChecksum) { Remove-Item -LiteralPath $zipChecksum -Force }

$pluginStage = Join-Path $stage 'Plugin'
$overlayStage = Join-Path $pluginStage 'overlay'
New-Item -ItemType Directory -Path $overlayStage -Force | Out-Null
Copy-Item -LiteralPath $artifact -Destination $pluginStage
Copy-Item -LiteralPath $wordTable -Destination $pluginStage
Copy-Item -LiteralPath (Join-Path $packaging 'KeyboundProbe.settings.ini') -Destination $pluginStage
foreach ($name in @('index.html', 'overlay.css', 'overlay.js')) {
    Copy-Item -LiteralPath (Join-Path $workspaceRoot "src\KeyboundProbe\overlay\$name") -Destination $overlayStage
}
foreach ($name in @('README.md', '使用说明.txt', '安装到呆萌ACT.cmd', '卸载.cmd',
        'Install-KeyboundProbe.ps1', 'Uninstall-KeyboundProbe.ps1')) {
    Copy-Item -LiteralPath (Join-Path $packaging $name) -Destination $stage
}
Copy-Item -LiteralPath $versionPath -Destination $stage
Copy-Item -LiteralPath $LicensePath -Destination (Join-Path $stage 'LICENSE')
$usagePath = Join-Path $stage '使用说明.txt'
$usage = [IO.File]::ReadAllText($usagePath).Replace('__VERSION__', $version)
[IO.File]::WriteAllText($usagePath, $usage, (New-Object Text.UTF8Encoding($false)))

$manifestPath = Join-Path $stage 'SHA256SUMS.txt'
$manifestLines = foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllLines($manifestPath, $manifestLines, (New-Object Text.UTF8Encoding($false)))

Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })
    foreach ($requiredName in @(
        "$packageName/README.md",
        "$packageName/LICENSE",
        "$packageName/VERSION",
        "$packageName/使用说明.txt",
        "$packageName/安装到呆萌ACT.cmd",
        "$packageName/Plugin/KeyboundProbe.ActPlugin.dll",
        "$packageName/Plugin/overlay/index.html"
    )) {
        if ($names -notcontains $requiredName) { throw "Archive verification failed; missing $requiredName" }
    }
    $forbidden = @($names | Where-Object {
        $_ -match '(?i)(runtime\.js$|\.pdb$|\.log$|/Captures/|/BridgeRuntime/)'
    })
    if ($forbidden.Count -ne 0) {
        throw "Archive contains forbidden runtime/private files: $($forbidden -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText($zipChecksum,
    $zipHash.ToLowerInvariant() + "  " + [IO.Path]::GetFileName($zip) + "`r`n",
    (New-Object Text.UTF8Encoding($false)))
Write-Host "Package: $zip"
Write-Host "Version: $version"
Write-Host "Files: $($manifestLines.Count + 1)"
Write-Host "SHA256: $zipHash"
