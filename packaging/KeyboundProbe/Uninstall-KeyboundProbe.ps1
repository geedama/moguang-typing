param(
    [string]$ActRoot
)

$ErrorActionPreference = 'Stop'

function Select-ActRoot([string]$InitialPath) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '请选择已安装魔光打字通的呆萌 ACT 根目录'
    $dialog.ShowNewFolderButton = $false
    if (-not [string]::IsNullOrWhiteSpace($InitialPath) -and (Test-Path -LiteralPath $InitialPath)) {
        $dialog.SelectedPath = $InitialPath
    }
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw '已取消卸载。' }
    return $dialog.SelectedPath
}

if ([string]::IsNullOrWhiteSpace($ActRoot)) { $ActRoot = Select-ActRoot $env:DIEMOE_ACT_ROOT }
$ActRoot = (Resolve-Path -LiteralPath $ActRoot).Path
$actExe = Join-Path $ActRoot 'Advanced Combat Tracker.exe'
if (-not (Test-Path -LiteralPath $actExe)) { throw '所选目录不是呆萌 ACT 根目录。' }
$actRootPrefix = $ActRoot.TrimEnd('\') + '\'
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    if ($_.ProcessName -ne 'Advanced Combat Tracker' -and $_.ProcessName -ne 'ACT.DieMoe.Launcher') {
        return $false
    }
    try { return $_.Path.StartsWith($actRootPrefix, [StringComparison]::OrdinalIgnoreCase) }
    catch { return $true }
})
if ($running.Count -ne 0) { throw '请先关闭所有 ACT/呆萌启动器实例再卸载。' }

$actConfig = Join-Path $ActRoot 'Config\Advanced Combat Tracker.config.xml'
if (Test-Path -LiteralPath $actConfig) {
    Copy-Item -LiteralPath $actConfig -Destination ($actConfig + '.keyboundprobe-uninstall.bak') -Force
    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($actConfig)
    foreach ($plugin in @($document.SelectNodes('/Config/ActPlugins/Plugin'))) {
        if ([string]::Equals($plugin.GetAttribute('Path'),
                'Plugins\KeyboundProbe\KeyboundProbe.ActPlugin.dll',
                [StringComparison]::OrdinalIgnoreCase)) {
            [void]$plugin.ParentNode.RemoveChild($plugin)
        }
    }
    $document.Save($actConfig)
}

$overlayConfig = Join-Path $ActRoot 'Config\RainbowMage.OverlayPlugin.config.json'
if (Test-Path -LiteralPath $overlayConfig) {
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
