param(
    [string]$ActRoot
)

$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$errorLog = Join-Path ([IO.Path]::GetTempPath()) 'MoguangTyping-Install-Error.log'

trap {
    $failure = $_
    try {
        $details = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')]`r`n$($failure | Out-String)"
        [IO.File]::WriteAllText($errorLog, $details, (New-Object Text.UTF8Encoding($true)))
    }
    catch { }
    Write-Host ''
    Write-Host ("安装失败：" + $failure.Exception.Message) -ForegroundColor Red
    Write-Host ("错误日志：" + $errorLog) -ForegroundColor Yellow
    exit 1
}

function Select-ActRoot([string]$InitialPath) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '请选择 ACT 根目录（支持原生 ACT、ACT 国服整合和呆萌 ACT）'
    $dialog.ShowNewFolderButton = $false
    if (-not [string]::IsNullOrWhiteSpace($InitialPath) -and (Test-Path -LiteralPath $InitialPath)) {
        $dialog.SelectedPath = $InitialPath
    }
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        throw '已取消安装。'
    }
    return $dialog.SelectedPath
}

function Resolve-ActLayout([string]$Path) {
    $actExe = Join-Path $Path 'Advanced Combat Tracker.exe'
    $cafeExe = Join-Path $Path 'CafeACT.exe'
    $hostType = $null
    $configCandidates = @()
    if ((Test-Path -LiteralPath $cafeExe) -and
        (Test-Path -LiteralPath (Join-Path $Path 'Advanced Combat Tracker.dll'))) {
        $hostType = 'ACT 国服整合'
        $configCandidates = @(
            (Join-Path $Path 'AppData\Advanced Combat Tracker\Config\CafeACT.config.xml'),
            (Join-Path $Path 'AppData\Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml')
        )
    }
    elseif ((Test-Path -LiteralPath $actExe) -and
        (Test-Path -LiteralPath (Join-Path $Path 'DLibs\Advanced Combat Tracker.dll'))) {
        $hostType = '呆萌 ACT'
        $configCandidates = @((Join-Path $Path 'Config\Advanced Combat Tracker.config.xml'))
    }
    elseif (Test-Path -LiteralPath $actExe) {
        $hostType = 'ACT 原生版'
        $configCandidates = @(
            (Join-Path $Path 'Config\Advanced Combat Tracker.config.xml'),
            (Join-Path $env:APPDATA 'Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml')
        )
    }
    else {
        throw '所选目录不是受支持的 ACT 根目录；应包含 Advanced Combat Tracker.exe 或 CafeACT.exe。'
    }

    $actConfig = @($configCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
    if ($actConfig.Count -eq 0) {
        throw "$hostType 尚未生成 ACT 配置。请先启动一次 ACT，正常退出后再安装。"
    }
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
        HostType = $hostType
        ActConfig = $actConfig
        OverlayConfig = if ($overlayConfig.Count -eq 0) { $null } else { $overlayConfig[0] }
    }
}

function Backup-Config([string]$Path, [string]$BackupDirectory) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
    $name = [IO.Path]::GetFileName($Path)
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item -LiteralPath $Path -Destination (Join-Path $BackupDirectory "$name.$stamp.bak") -Force
}

function Enable-ActPlugin([string]$ConfigPath, [string]$PluginPath) {
    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($ConfigPath)
    $container = $document.SelectSingleNode('/Config/ActPlugins')
    if ($null -eq $container) { throw 'ACT 配置中缺少 /Config/ActPlugins 节点。' }
    $fullPluginPath = [IO.Path]::GetFullPath($PluginPath)
    $plugin = $null
    foreach ($candidate in @($container.SelectNodes('Plugin'))) {
        $candidatePath = $candidate.GetAttribute('Path')
        if ($candidatePath -match '(?i)(^|[\\/])KeyboundProbe[\\/]KeyboundProbe\.ActPlugin\.dll$') {
            $plugin = $candidate
            break
        }
    }
    if ($null -eq $plugin) {
        $plugin = $document.CreateElement('Plugin')
        [void]$container.AppendChild($plugin)
    }
    $plugin.SetAttribute('Path', $fullPluginPath)
    $plugin.SetAttribute('Enabled', 'True')
    $document.Save($ConfigPath)
}

function Register-Overlay([string]$ConfigPath, [string]$OverlayIndex) {
    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        Write-Warning '未找到 OverlayPlugin 配置；ACT 插件仍已安装，悬浮窗需要稍后手动添加。'
        return $false
    }
    $raw = [IO.File]::ReadAllText($ConfigPath)
    $url = ([Uri]$OverlayIndex).AbsoluteUri
    $blockPattern = '(?s)\{[^{}]*"Name"\s*:\s*"(?:魔光打字通|魔光键影调试)"[^{}]*\}'
    $match = [regex]::Match($raw, $blockPattern)
    if ($match.Success) {
        $block = $match.Value
        $updated = [regex]::Replace($block, '"Name"\s*:\s*"[^"]*"', '"Name": "魔光打字通"', 1)
        $updated = [regex]::Replace($updated, '"Url"\s*:\s*"[^"]*"', '"Url": "' + $url + '"', 1)
        $updated = [regex]::Replace($updated, '"Disabled"\s*:\s*(true|false)', '"Disabled": false', 1)
        $updated = [regex]::Replace($updated, '"IsVisible"\s*:\s*(true|false)', '"IsVisible": true', 1)
        $raw = $raw.Substring(0, $match.Index) + $updated +
            $raw.Substring($match.Index + $match.Length)
    }
    else {
        $typeMatch = [regex]::Match($raw,
            '"\$type"\s*:\s*"([^"]*MiniParseOverlayConfig[^"]*)"')
        $overlayType = if ($typeMatch.Success) { $typeMatch.Groups[1].Value } else {
            'RainbowMage.OverlayPlugin.Overlays.MiniParseOverlayConfig, OverlayPlugin.Core, Version=0.19.110.0, Culture=neutral, PublicKeyToken=null'
        }
        $entry = @'
    {
      "forceWhiteBackground": false,
      "GlobalHotkeys": [],
      "ActwsCompatibility": false,
      "NoFocus": true,
      "Zoom": 0,
      "ForceWhiteBackground": false,
      "MuteWhenHidden": false,
      "Name": "魔光打字通",
      "Uuid": "79e86cec-2e26-4a0e-a8a4-59ca509317d4",
      "IsVisible": true,
      "Disabled": false,
      "IsClickThru": false,
      "Position": "80, 80",
      "Size": "520, 310",
      "Url": "__OVERLAY_URL__",
      "MaxFrameRate": 30,
      "GlobalHotkeyEnabled": false,
      "GlobalHotkey": 0,
      "GlobalHotkeyModifiers": 0,
      "GlobalHotkeyType": 0,
      "IsLocked": false,
      "LogConsoleMessages": true,
      "HideOutOfCombat": false,
      "$type": "__OVERLAY_TYPE__"
    }
'@
        $entry = $entry.Replace('__OVERLAY_URL__', $url)
        $entry = $entry.Replace('__OVERLAY_TYPE__', $overlayType)
        $marker = [regex]::Match($raw, '(?m)^\s*\],\r?\n\s*"FollowLatestLog"')
        if (-not $marker.Success) {
            Write-Warning '无法识别当前 OverlayPlugin 配置格式；请按 README 手动添加悬浮窗。'
            return $false
        }
        $overlaysName = $raw.LastIndexOf('"Overlays"', $marker.Index,
            [StringComparison]::OrdinalIgnoreCase)
        if ($overlaysName -lt 0) {
            Write-Warning 'OverlayPlugin 配置中没有 Overlays 数组；请按 README 手动添加悬浮窗。'
            return $false
        }
        $arrayOpen = $raw.IndexOf('[', $overlaysName)
        if ($arrayOpen -lt 0 -or $arrayOpen -gt $marker.Index) {
            Write-Warning 'OverlayPlugin 的 Overlays 数组格式异常；请按 README 手动添加悬浮窗。'
            return $false
        }
        $hasExisting = $raw.Substring($arrayOpen + 1, $marker.Index - $arrayOpen - 1).Trim().Length -gt 0
        $prefix = if ($hasExisting) { ",`r`n" } else { "`r`n" }
        $raw = $raw.Insert($marker.Index, $prefix + $entry.TrimEnd() + "`r`n")
    }
    [IO.File]::WriteAllText($ConfigPath, $raw, (New-Object Text.UTF8Encoding($false)))
    return $true
}

$payload = Join-Path $packageRoot 'Plugin'
if (-not (Test-Path -LiteralPath (Join-Path $payload 'KeyboundProbe.ActPlugin.dll'))) {
    throw '安装包不完整：缺少 Plugin\KeyboundProbe.ActPlugin.dll。请重新解压完整 ZIP。'
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
if ($running.Count -ne 0) {
    throw "请先关闭所有 ACT/呆萌启动器实例再安装。当前 PID：$($running.Id -join ', ')"
}

$pluginDirectory = Join-Path $ActRoot 'Plugins\KeyboundProbe'
$backupDirectory = Join-Path (Split-Path -Parent $layout.ActConfig) 'KeyboundProbe.Backups'
$actConfig = $layout.ActConfig
$overlayConfig = $layout.OverlayConfig
Backup-Config $actConfig $backupDirectory
if ($null -ne $overlayConfig) { Backup-Config $overlayConfig $backupDirectory }

New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $payload 'KeyboundProbe.ActPlugin.dll') -Destination $pluginDirectory -Force
Copy-Item -LiteralPath (Join-Path $payload 'KeyboundProbe.words.tsv') -Destination $pluginDirectory -Force
$settingsTarget = Join-Path $pluginDirectory 'KeyboundProbe.settings.ini'
if (-not (Test-Path -LiteralPath $settingsTarget)) {
    Copy-Item -LiteralPath (Join-Path $payload 'KeyboundProbe.settings.ini') -Destination $settingsTarget
}
$overlayTarget = Join-Path $pluginDirectory 'overlay'
New-Item -ItemType Directory -Path $overlayTarget -Force | Out-Null
foreach ($name in @('index.html', 'overlay.css', 'overlay.js')) {
    Copy-Item -LiteralPath (Join-Path $payload "overlay\$name") -Destination $overlayTarget -Force
}
Get-ChildItem -LiteralPath $pluginDirectory -File -Recurse | Unblock-File -ErrorAction SilentlyContinue

$pluginPath = Join-Path $pluginDirectory 'KeyboundProbe.ActPlugin.dll'
Enable-ActPlugin $actConfig $pluginPath
$overlayInstalled = if ($null -eq $overlayConfig) {
    Write-Warning '未找到 OverlayPlugin 配置；ACT 插件仍已安装，悬浮窗需要稍后手动添加。'
    $false
} else { Register-Overlay $overlayConfig (Join-Path $overlayTarget 'index.html') }

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Join-Path $pluginDirectory 'KeyboundProbe.ActPlugin.dll')).FileVersion
Write-Host ''
Write-Host "安装完成：魔光打字通 $version" -ForegroundColor Green
Write-Host "ACT 类型：$($layout.HostType)"
Write-Host "ACT 目录：$ActRoot"
Write-Host "ACT 配置：$actConfig"
Write-Host "插件目录：$pluginDirectory"
Write-Host "悬浮窗：$(if ($overlayInstalled) { '已注册' } else { '未自动注册，请查看 README' })"
Write-Host '现在可启动 ACT；进入游戏后在插件页或悬浮窗中开启自动攻击。'
