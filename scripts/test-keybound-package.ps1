param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [string]$TemplateActRoot
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($TemplateActRoot)) {
    $TemplateActRoot = Join-Path $workspaceRoot 'NewMoe-Technology\ACT.DieMoe'
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$TemplateActRoot = (Resolve-Path -LiteralPath $TemplateActRoot).Path
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("KeyboundProbe-PackageTest-" + [Guid]::NewGuid().ToString('N'))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$resolvedFixture = [IO.Path]::GetFullPath($fixture)
if (-not $resolvedFixture.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedFixture) -notlike 'KeyboundProbe-PackageTest-*') {
    throw "Unsafe fixture path: $resolvedFixture"
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Package integration test failed: $Message" }
}

foreach ($name in @('安装到呆萌ACT.cmd', '卸载.cmd')) {
    $cmdPath = Join-Path $PackageDirectory $name
    $bytes = [IO.File]::ReadAllBytes($cmdPath)
    Assert-True (-not @($bytes | Where-Object { $_ -gt 0x7F }).Count) `
        "$name must contain ASCII bytes only for cmd.exe compatibility"
}

foreach ($name in @('Install-KeyboundProbe.ps1', 'Uninstall-KeyboundProbe.ps1')) {
    $scriptPath = Join-Path $PackageDirectory $name
    $bytes = [IO.File]::ReadAllBytes($scriptPath)
    Assert-True ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) "$name is not UTF-8 BOM"

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $escapedPath = $scriptPath.Replace("'", "''")
    $parseCommand = "`$tokens=`$null; `$errors=`$null; " +
        "[System.Management.Automation.Language.Parser]::ParseFile('$escapedPath', " +
        "[ref]`$tokens, [ref]`$errors) > `$null; " +
        "if (`$errors.Count -ne 0) { `$errors | ForEach-Object { Write-Error `$_.Message }; exit 1 }"
    & $windowsPowerShell -NoProfile -NonInteractive -Command $parseCommand
    Assert-True ($LASTEXITCODE -eq 0) "$name does not parse in Windows PowerShell 5.1"
}

function Invoke-PackageScript([string]$Name, [string]$ActRoot) {
    $scriptPath = Join-Path $PackageDirectory $Name
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File $scriptPath -ActRoot $ActRoot
    Assert-True ($LASTEXITCODE -eq 0) "$Name failed in Windows PowerShell 5.1"
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'DLibs'),
        (Join-Path $fixture 'Config') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $fixture 'Advanced Combat Tracker.exe'),
        (Join-Path $fixture 'DLibs\Advanced Combat Tracker.dll') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $TemplateActRoot 'Config\Advanced Combat Tracker.config.xml') `
        -Destination (Join-Path $fixture 'Config')
    Copy-Item -LiteralPath (Join-Path $TemplateActRoot 'Config\RainbowMage.OverlayPlugin.config.json') `
        -Destination (Join-Path $fixture 'Config')

    Invoke-PackageScript 'Install-KeyboundProbe.ps1' $fixture
    $plugin = Join-Path $fixture 'Plugins\KeyboundProbe\KeyboundProbe.ActPlugin.dll'
    $settings = Join-Path $fixture 'Plugins\KeyboundProbe\KeyboundProbe.settings.ini'
    Assert-True (Test-Path -LiteralPath $plugin) 'plugin DLL was not copied'
    Assert-True (Test-Path -LiteralPath $settings) 'default settings were not copied'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture 'Plugins\KeyboundProbe\overlay\runtime.js'))) `
        'runtime token file must not be shipped'

    $actDocument = New-Object System.Xml.XmlDocument
    $actDocument.Load((Join-Path $fixture 'Config\Advanced Combat Tracker.config.xml'))
    $pluginNodes = @($actDocument.SelectNodes('/Config/ActPlugins/Plugin') | Where-Object {
        $_.GetAttribute('Path') -eq 'Plugins\KeyboundProbe\KeyboundProbe.ActPlugin.dll' -and
        $_.GetAttribute('Enabled') -eq 'True'
    })
    Assert-True ($pluginNodes.Count -eq 1) 'ACT plugin registration is missing or duplicated'

    $overlayRaw = [IO.File]::ReadAllText((Join-Path $fixture 'Config\RainbowMage.OverlayPlugin.config.json'))
    $overlayMatch = [regex]::Match($overlayRaw,
        '(?s)\{[^{}]*"Name"\s*:\s*"魔光打字通"[^{}]*\}')
    Assert-True $overlayMatch.Success 'ngld overlay registration is missing'
    Assert-True ($overlayMatch.Value -match [regex]::Escape(([Uri](Join-Path $fixture `
        'Plugins\KeyboundProbe\overlay\index.html')).AbsoluteUri)) 'ngld overlay URL is incorrect'

    [IO.File]::AppendAllText($settings, "`r`n# preserve-sentinel", (New-Object Text.UTF8Encoding($false)))
    Invoke-PackageScript 'Install-KeyboundProbe.ps1' $fixture
    Assert-True ([IO.File]::ReadAllText($settings).Contains('# preserve-sentinel')) `
        'upgrade overwrote existing user settings'

    Invoke-PackageScript 'Uninstall-KeyboundProbe.ps1' $fixture
    Assert-True (-not (Test-Path -LiteralPath $plugin)) 'uninstall left the plugin DLL behind'
    Assert-True (Test-Path -LiteralPath $settings) 'uninstall removed user settings'
    $actDocument.Load((Join-Path $fixture 'Config\Advanced Combat Tracker.config.xml'))
    $pluginNodes = @($actDocument.SelectNodes('/Config/ActPlugins/Plugin') | Where-Object {
        $_.GetAttribute('Path') -eq 'Plugins\KeyboundProbe\KeyboundProbe.ActPlugin.dll'
    })
    Assert-True ($pluginNodes.Count -eq 0) 'uninstall left ACT plugin registration behind'
    $overlayRaw = [IO.File]::ReadAllText((Join-Path $fixture 'Config\RainbowMage.OverlayPlugin.config.json'))
    $overlayMatch = [regex]::Match($overlayRaw,
        '(?s)\{[^{}]*"Name"\s*:\s*"魔光打字通"[^{}]*\}')
    Assert-True ($overlayMatch.Success -and $overlayMatch.Value -match '"Disabled"\s*:\s*true' -and
        $overlayMatch.Value -match '"IsVisible"\s*:\s*false') 'uninstall did not disable the overlay'

    Write-Host 'PASS: KeyboundProbe package install/upgrade/uninstall integration test'
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
