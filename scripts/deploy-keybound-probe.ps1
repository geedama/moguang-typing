param(
    [string]$DieMoeRoot,
    [string]$WordTablePath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DieMoeRoot)) {
    $DieMoeRoot = Join-Path $workspaceRoot 'NewMoe-Technology\ACT.DieMoe'
}
$DieMoeRoot = (Resolve-Path -LiteralPath $DieMoeRoot).Path
$actAssembly = Join-Path $DieMoeRoot 'DLibs\Advanced Combat Tracker.dll'
$actConfig = Join-Path $DieMoeRoot 'Config\Advanced Combat Tracker.config.xml'
$artifact = Join-Path $workspaceRoot 'src\KeyboundProbe\artifacts\KeyboundProbe.ActPlugin.dll'
$defaultWordTable = Join-Path $workspaceRoot 'src\KeyboundProbe\artifacts\KeyboundProbe.words.tsv'
$overlaySource = Join-Path $workspaceRoot 'src\KeyboundProbe\overlay'
$wordTable = if ([string]::IsNullOrWhiteSpace($WordTablePath)) { $defaultWordTable } else { $WordTablePath }
$pluginDirectory = Join-Path $DieMoeRoot 'Plugins\KeyboundProbe'

$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -eq 'Advanced Combat Tracker' -or $_.ProcessName -eq 'ACT.DieMoe.Launcher'
})
if ($running.Count -ne 0) {
    throw "Close DieMoe ACT before deployment. Running PID(s): $($running.Id -join ', ')"
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-keybound-probe.ps1') -ActAssemblyPath $actAssembly
    if ($LASTEXITCODE -ne 0) { throw 'KeyboundProbe build failed.' }
}
foreach ($required in @($artifact, $wordTable, $actConfig, $overlaySource)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required file not found: $required" }
}

New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -LiteralPath $artifact -Destination $pluginDirectory -Force
Copy-Item -LiteralPath $wordTable -Destination (Join-Path $pluginDirectory 'KeyboundProbe.words.tsv') -Force
$overlayDirectory = Join-Path $pluginDirectory 'overlay'
New-Item -ItemType Directory -Path $overlayDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $overlaySource '*') -Destination $overlayDirectory -Recurse -Force
$pdb = [IO.Path]::ChangeExtension($artifact, '.pdb')
if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $pluginDirectory -Force }

$timestamp = Get-Date -Format 'yyyyMMddHHmmss'
Copy-Item -LiteralPath $actConfig -Destination "$actConfig.keyboundprobe-$timestamp.bak"
$document = New-Object System.Xml.XmlDocument
$document.PreserveWhitespace = $true
$document.Load($actConfig)
$pluginPath = 'Plugins\KeyboundProbe\KeyboundProbe.ActPlugin.dll'
$node = @($document.SelectNodes('/Config/ActPlugins/Plugin')) |
    Where-Object { $_.GetAttribute('Path') -eq $pluginPath } | Select-Object -First 1
if ($null -eq $node) {
    $node = $document.CreateElement('Plugin')
    $node.SetAttribute('Path', $pluginPath)
    [void]$document.SelectSingleNode('/Config/ActPlugins').AppendChild($node)
}
$node.SetAttribute('Enabled', 'True')
$document.Save($actConfig)

$overlayConfig = Join-Path $DieMoeRoot 'Config\RainbowMage.OverlayPlugin.config.json'
if (Test-Path -LiteralPath $overlayConfig) {
    Copy-Item -LiteralPath $overlayConfig -Destination "$overlayConfig.keyboundprobe-$timestamp.bak"
    $raw = [IO.File]::ReadAllText($overlayConfig)
    $legacyPattern = '(?s)\{[^{}]*"Name"\s*:\s*"魔光键影调试"[^{}]*\}'
    $legacyMatch = [regex]::Match($raw, $legacyPattern)
    if ($legacyMatch.Success) {
        $renamed = [regex]::Replace($legacyMatch.Value,
            '"Name"\s*:\s*"魔光键影调试"', '"Name": "魔光打字通"', 1)
        $raw = $raw.Substring(0, $legacyMatch.Index) + $renamed +
            $raw.Substring($legacyMatch.Index + $legacyMatch.Length)
        [IO.File]::WriteAllText($overlayConfig, $raw, [Text.UTF8Encoding]::new($false))
    }
    elseif ($raw -notmatch '"Name"\s*:\s*"魔光打字通"') {
        $overlayUrl = ([Uri](Join-Path $overlayDirectory 'index.html')).AbsoluteUri
        $entry = @"
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
      "Position": "1150, 180",
      "Size": "520, 310",
      "Url": "$overlayUrl",
      "MaxFrameRate": 30,
      "GlobalHotkeyEnabled": false,
      "GlobalHotkey": 0,
      "GlobalHotkeyModifiers": 0,
      "GlobalHotkeyType": 0,
      "IsLocked": false,
      "LogConsoleMessages": true,
      "HideOutOfCombat": false,
      "`$type": "RainbowMage.OverlayPlugin.Overlays.MiniParseOverlayConfig, OverlayPlugin.Core, Version=0.19.110.0, Culture=neutral, PublicKeyToken=null"
    }
"@
        $marker = '(?m)^  \],\r?\n  "FollowLatestLog"'
        if ($raw -notmatch $marker) { throw 'OverlayPlugin config insertion point not found.' }
        $replacement = ",`r`n" + $entry + "  ],`r`n  `"FollowLatestLog`""
        $raw = [regex]::Replace($raw, $marker, $replacement, 1)
        [IO.File]::WriteAllText($overlayConfig, $raw, [Text.UTF8Encoding]::new($false))
    }
}

Write-Host "KeyboundProbe deployed: $pluginDirectory"
Write-Host "Word table deployed: $wordTable"
Write-Host "ngld overlay deployed: $overlayDirectory"
Write-Host "ACT plugin enabled: $pluginPath"
