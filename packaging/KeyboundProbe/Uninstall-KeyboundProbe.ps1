param(
    [string]$ActRoot
)

$ErrorActionPreference = 'Stop'
$errorLog = Join-Path ([IO.Path]::GetTempPath()) 'MoguangTyping-Uninstall-Error.log'

trap {
    $failure = $_
    try {
        $details = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')]`r`n$($failure | Out-String)"
        [IO.File]::WriteAllText($errorLog, $details, (New-Object Text.UTF8Encoding($true)))
    }
    catch { }
    Write-Host ''
    Write-Host ("卸载失败：" + $failure.Exception.Message) -ForegroundColor Red
    Write-Host ("错误日志：" + $errorLog) -ForegroundColor Yellow
    exit 1
}

function Select-ActRoot([string]$InitialPath) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '请选择已安装魔光打字通的 ACT 根目录'
    $dialog.ShowNewFolderButton = $false
    if (-not [string]::IsNullOrWhiteSpace($InitialPath) -and (Test-Path -LiteralPath $InitialPath)) {
        $dialog.SelectedPath = $InitialPath
    }
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw '已取消卸载。' }
    return $dialog.SelectedPath
}

function Resolve-ActLayout([string]$Path) {
    $actExe = Join-Path $Path 'Advanced Combat Tracker.exe'
    $cafeExe = Join-Path $Path 'CafeACT.exe'
    if ((Test-Path -LiteralPath $cafeExe) -and
        (Test-Path -LiteralPath (Join-Path $Path 'Advanced Combat Tracker.dll'))) {
        $configCandidates = @(
            (Join-Path $Path 'AppData\Advanced Combat Tracker\Config\CafeACT.config.xml'),
            (Join-Path $Path 'AppData\Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml')
        )
    }
    elseif (Test-Path -LiteralPath $actExe) {
        $configCandidates = @(
            (Join-Path $Path 'Config\Advanced Combat Tracker.config.xml'),
            (Join-Path $env:APPDATA 'Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml')
        )
    }
    else { throw '所选目录不是受支持的 ACT 根目录。' }
    $actConfig = @($configCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
    if ($actConfig.Count -eq 0) { throw '没有找到该 ACT 使用的配置文件。' }
    $actConfig = $actConfig[0]
    $configDirectory = Split-Path -Parent $actConfig
    $overlayCandidates = @(
        (Join-Path $configDirectory 'RainbowMage.OverlayPlugin.config.json'),
        (Join-Path $Path 'Config\RainbowMage.OverlayPlugin.config.json'),
        (Join-Path $Path 'AppData\Advanced Combat Tracker\Config\RainbowMage.OverlayPlugin.config.json')
    )
    $overlayConfig = @($overlayCandidates | Select-Object -Unique |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
    [PSCustomObject]@{
        ActConfig = $actConfig
        OverlayConfig = if ($overlayConfig.Count -eq 0) { $null } else { $overlayConfig[0] }
    }
}

if ([string]::IsNullOrWhiteSpace($ActRoot)) {
    $initialRoot = if (-not [string]::IsNullOrWhiteSpace($env:MOGUANG_ACT_ROOT)) {
        $env:MOGUANG_ACT_ROOT
    } else { $env:DIEMOE_ACT_ROOT }
    $ActRoot = Select-ActRoot $initialRoot
}
$ActRoot = (Resolve-Path -LiteralPath $ActRoot).Path
$layout = Resolve-ActLayout $ActRoot
$actRootPrefix = $ActRoot.TrimEnd('\') + '\'
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    if ($_.ProcessName -ne 'Advanced Combat Tracker' -and $_.ProcessName -ne 'ACT.DieMoe.Launcher' -and
        $_.ProcessName -ne 'CafeACT') {
        return $false
    }
    try { return $_.Path.StartsWith($actRootPrefix, [StringComparison]::OrdinalIgnoreCase) }
    catch { return $true }
})
if ($running.Count -ne 0) { throw '请先关闭所有 ACT/呆萌启动器实例再卸载。' }

$actConfig = $layout.ActConfig
if (Test-Path -LiteralPath $actConfig) {
    Copy-Item -LiteralPath $actConfig -Destination ($actConfig + '.keyboundprobe-uninstall.bak') -Force
    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($actConfig)
    foreach ($plugin in @($document.SelectNodes('/Config/ActPlugins/Plugin'))) {
        if ($plugin.GetAttribute('Path') -match
            '(?i)(^|[\\/])KeyboundProbe[\\/]KeyboundProbe\.ActPlugin\.dll$') {
            [void]$plugin.ParentNode.RemoveChild($plugin)
        }
    }
    $document.Save($actConfig)
}

$overlayConfig = $layout.OverlayConfig
if ($null -ne $overlayConfig -and (Test-Path -LiteralPath $overlayConfig)) {
    Copy-Item -LiteralPath $overlayConfig -Destination ($overlayConfig + '.keyboundprobe-uninstall.bak') -Force
    $raw = [IO.File]::ReadAllText($overlayConfig)
    $pattern = '(?s)\{[^{}]*"Name"\s*:\s*"(?:魔光打字通|魔光键影调试)"[^{}]*\}'
    $match = [regex]::Match($raw, $pattern)
    if ($match.Success) {
        $block = [regex]::Replace($match.Value, '"Disabled"\s*:\s*(true|false)', '"Disabled": true', 1)
        $block = [regex]::Replace($block, '"IsVisible"\s*:\s*(true|false)', '"IsVisible": false', 1)
        $raw = $raw.Substring(0, $match.Index) + $block + $raw.Substring($match.Index + $match.Length)
        [IO.File]::WriteAllText($overlayConfig, $raw, (New-Object Text.UTF8Encoding($false)))
    }
}

$pluginDirectory = Join-Path $ActRoot 'Plugins\KeyboundProbe'
foreach ($relative in @(
    'KeyboundProbe.ActPlugin.dll', 'KeyboundProbe.words.tsv',
    'overlay\index.html', 'overlay\overlay.css', 'overlay\overlay.js', 'overlay\runtime.js'
)) {
    $target = Join-Path $pluginDirectory $relative
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
}

Write-Host '卸载完成。用户设置、日志和抓包文件已保留在 Plugins\KeyboundProbe。' -ForegroundColor Green
