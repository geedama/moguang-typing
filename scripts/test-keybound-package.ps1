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
$fixtureParent = Join-Path $workspaceRoot '.tmp-package-tests'
$fixture = Join-Path $fixtureParent ("KeyboundProbe-PackageTest-" + [Guid]::NewGuid().ToString('N'))
$tempPrefix = [IO.Path]::GetFullPath($fixtureParent).TrimEnd('\') + '\'
$resolvedFixture = [IO.Path]::GetFullPath($fixture)
if (-not $resolvedFixture.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedFixture) -notlike 'KeyboundProbe-PackageTest-*') {
    throw "Unsafe fixture path: $resolvedFixture"
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Package integration test failed: $Message" }
}

foreach ($name in @('安装到ACT.cmd', '安装到呆萌ACT.cmd', '卸载.cmd')) {
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

function Copy-TestConfig([string]$Destination, [string]$FileName) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $TemplateActRoot 'Config\Advanced Combat Tracker.config.xml') `
        -Destination $Destination
    if ([IO.Path]::GetFileName($Destination) -ne $FileName) {
        throw "Fixture config destination mismatch: $Destination"
    }
}

function Invoke-HostMatrixCase([string]$CaseName, [string]$HostType) {
    $caseRoot = Join-Path $fixture $CaseName
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    $originalAppData = $env:APPDATA
    $overlayPath = $null
    try {
        if ($HostType -eq 'DieMoe') {
            New-Item -ItemType Directory -Path (Join-Path $caseRoot 'DLibs'),
                (Join-Path $caseRoot 'Config') -Force | Out-Null
            New-Item -ItemType File -Path (Join-Path $caseRoot 'Advanced Combat Tracker.exe'),
                (Join-Path $caseRoot 'DLibs\Advanced Combat Tracker.dll') -Force | Out-Null
            $configPath = Join-Path $caseRoot 'Config\Advanced Combat Tracker.config.xml'
            Copy-TestConfig $configPath 'Advanced Combat Tracker.config.xml'
            Copy-Item -LiteralPath (Join-Path $TemplateActRoot 'Config\RainbowMage.OverlayPlugin.config.json') `
                -Destination (Join-Path $caseRoot 'Config')
            $overlayPath = Join-Path $caseRoot 'Config\RainbowMage.OverlayPlugin.config.json'
        }
        elseif ($HostType -eq 'NativePortable') {
            New-Item -ItemType Directory -Path (Join-Path $caseRoot 'Config') -Force | Out-Null
            New-Item -ItemType File -Path (Join-Path $caseRoot 'Advanced Combat Tracker.exe') -Force | Out-Null
            $configPath = Join-Path $caseRoot 'Config\Advanced Combat Tracker.config.xml'
            Copy-TestConfig $configPath 'Advanced Combat Tracker.config.xml'
        }
        elseif ($HostType -eq 'NativeAppData') {
            New-Item -ItemType File -Path (Join-Path $caseRoot 'Advanced Combat Tracker.exe') -Force | Out-Null
            $env:APPDATA = Join-Path $caseRoot 'UserAppData'
            $configPath = Join-Path $env:APPDATA 'Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml'
            Copy-TestConfig $configPath 'Advanced Combat Tracker.config.xml'
        }
        elseif ($HostType -eq 'Cafe') {
            New-Item -ItemType File -Path (Join-Path $caseRoot 'CafeACT.exe'),
                (Join-Path $caseRoot 'Advanced Combat Tracker.dll') -Force | Out-Null
            $configPath = Join-Path $caseRoot 'AppData\Advanced Combat Tracker\Config\CafeACT.config.xml'
            Copy-TestConfig $configPath 'CafeACT.config.xml'
            Copy-Item -LiteralPath (Join-Path $TemplateActRoot 'Config\RainbowMage.OverlayPlugin.config.json') `
                -Destination (Split-Path -Parent $configPath)
            $overlayPath = Join-Path (Split-Path -Parent $configPath) `
                'RainbowMage.OverlayPlugin.config.json'
        }
        else { throw "Unknown host test type: $HostType" }

        Invoke-PackageScript 'Install-KeyboundProbe.ps1' $caseRoot
        $plugin = Join-Path $caseRoot 'Plugins\KeyboundProbe\KeyboundProbe.ActPlugin.dll'
        $settings = Join-Path $caseRoot 'Plugins\KeyboundProbe\KeyboundProbe.settings.ini'
        Assert-True (Test-Path -LiteralPath $plugin) "$CaseName plugin DLL was not copied"
        Assert-True (Test-Path -LiteralPath $settings) "$CaseName settings were not copied"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $caseRoot `
            'Plugins\KeyboundProbe\overlay\runtime.js'))) "$CaseName shipped runtime token"

        $actDocument = New-Object System.Xml.XmlDocument
        $actDocument.Load($configPath)
        $expectedPluginPath = [IO.Path]::GetFullPath($plugin)
        $pluginNodes = @($actDocument.SelectNodes('/Config/ActPlugins/Plugin') | Where-Object {
            $_.GetAttribute('Path') -eq $expectedPluginPath -and $_.GetAttribute('Enabled') -eq 'True'
        })
        Assert-True ($pluginNodes.Count -eq 1) "$CaseName ACT registration is missing or duplicated"
        if ($null -ne $overlayPath) {
            $overlayRaw = [IO.File]::ReadAllText($overlayPath)
            $overlayMatch = [regex]::Match($overlayRaw,
                '(?s)\{[^{}]*"Name"\s*:\s*"魔光打字通"[^{}]*\}')
            Assert-True $overlayMatch.Success "$CaseName overlay registration is missing"
            $overlayUrl = ([Uri](Join-Path $caseRoot `
                'Plugins\KeyboundProbe\overlay\index.html')).AbsoluteUri
            Assert-True ($overlayMatch.Value -match [regex]::Escape($overlayUrl)) `
                "$CaseName overlay URL is incorrect"
        }

        [IO.File]::AppendAllText($settings, "`r`n# preserve-sentinel",
            (New-Object Text.UTF8Encoding($false)))
        Invoke-PackageScript 'Install-KeyboundProbe.ps1' $caseRoot
        Assert-True ([IO.File]::ReadAllText($settings).Contains('# preserve-sentinel')) `
            "$CaseName upgrade overwrote user settings"

        Invoke-PackageScript 'Uninstall-KeyboundProbe.ps1' $caseRoot
        Assert-True (-not (Test-Path -LiteralPath $plugin)) "$CaseName uninstall left plugin DLL"
        Assert-True (Test-Path -LiteralPath $settings) "$CaseName uninstall removed settings"
        $actDocument.Load($configPath)
        $pluginNodes = @($actDocument.SelectNodes('/Config/ActPlugins/Plugin') | Where-Object {
            $_.GetAttribute('Path') -match '(?i)KeyboundProbe[\\/]KeyboundProbe\.ActPlugin\.dll$'
        })
        Assert-True ($pluginNodes.Count -eq 0) "$CaseName uninstall left ACT registration"
        if ($null -ne $overlayPath) {
            $overlayRaw = [IO.File]::ReadAllText($overlayPath)
            $overlayMatch = [regex]::Match($overlayRaw,
                '(?s)\{[^{}]*"Name"\s*:\s*"魔光打字通"[^{}]*\}')
            Assert-True ($overlayMatch.Success -and
                $overlayMatch.Value -match '"Disabled"\s*:\s*true' -and
                $overlayMatch.Value -match '"IsVisible"\s*:\s*false') `
                "$CaseName uninstall did not disable overlay"
        }
    }
    finally {
        $env:APPDATA = $originalAppData
    }
}

try {
    Invoke-HostMatrixCase 'diemoe' 'DieMoe'
    Invoke-HostMatrixCase 'native-portable' 'NativePortable'
    Invoke-HostMatrixCase 'native-appdata' 'NativeAppData'
    Invoke-HostMatrixCase 'cafe' 'Cafe'
    Write-Host 'PASS: package install/upgrade/uninstall matrix (DieMoe/native/CafeACT)'
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
