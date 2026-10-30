param(
    [string]$ActAssemblyPath
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $workspaceRoot 'src\KeyboundProbe'
$artifacts = Join-Path $source 'artifacts'
$versionPath = Join-Path $workspaceRoot 'VERSION'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$actAssembly = if ([string]::IsNullOrWhiteSpace($ActAssemblyPath)) {
    Join-Path $workspaceRoot 'act-cnzhenghe\Advanced Combat Tracker.dll'
} else {
    $ActAssemblyPath
}

foreach ($required in @($compiler, $actAssembly, $versionPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required build input not found: $required"
    }
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$version = [IO.File]::ReadAllText($versionPath).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION must use semantic version format X.Y.Z: $version"
}
$assemblyVersion = $version + '.0'
$generatedVersion = Join-Path $artifacts 'GeneratedVersionInfo.cs'
$generatedVersionSource = @"
using System.Reflection;
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$version")]
"@
[IO.File]::WriteAllText($generatedVersion, $generatedVersionSource,
    (New-Object Text.UTF8Encoding($false)))
$core = Join-Path $source 'KeyboundProbeCore.cs'
$pluginSources = @(
    $core,
    $generatedVersion,
    (Join-Path $source 'KeyboundCombatLogParser.cs'),
    (Join-Path $source 'KeyboundDecoder.cs'),
    (Join-Path $source 'KtgProcessMemoryReader.cs'),
    (Join-Path $source 'KeyboundActSettings.cs'),
    (Join-Path $source 'GameInputSender.cs'),
    (Join-Path $source 'KeyboundOverlayServer.cs'),
    (Join-Path $source 'KeyboundProbePlugin.cs'),
    (Join-Path $source 'AssemblyInfo.cs')
)

& $compiler /nologo /target:library /optimize+ /debug:pdbonly `
    "/out:$artifacts\KeyboundProbe.ActPlugin.dll" `
    "/reference:$actAssembly" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    $pluginSources
if ($LASTEXITCODE -ne 0) { throw 'KeyboundProbe plugin build failed.' }

& $compiler /nologo /target:exe /optimize+ `
    "/out:$artifacts\KeyboundProbe.Tests.exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll `
    $core (Join-Path $source 'KeyboundDecoder.cs') (Join-Path $source 'KtgProcessMemoryReader.cs') `
    (Join-Path $source 'KeyboundCombatLogParser.cs') `
    (Join-Path $source 'KeyboundBridgeClient.cs') `
    (Join-Path $source 'KeyboundActSettings.cs') `
    (Join-Path $source 'DalamudAutoLauncher.cs') (Join-Path $source 'GameInputSender.cs') `
    (Join-Path $source 'KeyboundOverlayServer.cs') `
    (Join-Path $source 'KeyboundProbe.Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'KeyboundProbe tests build failed.' }

Write-Host "魔光打字通 build complete: $artifacts (version $version)"
Write-Host "ACT API reference: $actAssembly"
