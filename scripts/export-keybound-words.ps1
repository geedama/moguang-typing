param(
    [string]$SqpackPath = 'E:\WeGameApps\rail_apps\ffxiv(2000340)\game\sqpack',
    [string]$OutputPath,
    [string]$InitialsPath = 'E:\ff14ciku\魔光词库_首字母.tsv'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot 'src\KeyboundProbe\artifacts\KeyboundProbe.words.tsv'
}

foreach ($required in @($SqpackPath, (Join-Path $workspaceRoot 'tools\keybound-data-probe\keybound-data-probe.csproj'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required input not found: $required" }
}

$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspaceRoot '.nuget-packages'
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null

dotnet run --project (Join-Path $workspaceRoot 'tools\keybound-data-probe\keybound-data-probe.csproj') `
    --configuration Release -- $SqpackPath --export-words $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'Keybound word export failed.' }

$initials = @{}
if (Test-Path -LiteralPath $InitialsPath) {
    Get-Content -LiteralPath $InitialsPath | ForEach-Object {
        $fields = $_ -split "`t", 3
        if ($fields.Count -ge 2) { $initials[$fields[0]] = $fields[1].Trim().ToLowerInvariant() }
    }
}

$merged = foreach ($line in Get-Content -LiteralPath $OutputPath) {
    if ($line.StartsWith('#')) {
        "# row_id`tdisplay_text`tinput_text`tinput_keys"
        continue
    }
    $fields = $line -split "`t", 3
    if ($fields.Count -lt 3) { continue }
    $nameBuilder = [Text.StringBuilder]::new()
    foreach ($value in $fields[1].ToCharArray()) {
        if ($value -ge [char]0x4e00 -and $value -le [char]0x9fff) { [void]$nameBuilder.Append($value) }
    }
    $name = $nameBuilder.ToString()
    if ($initials.ContainsKey($name)) {
        $keys = $initials[$name]
    } else {
        $keys = -join ($fields[2].ToCharArray() | Where-Object { $_ -ge 'A' -and $_ -le 'Z' })
        $keys = $keys.ToLowerInvariant()
    }
    "$line`t$keys"
}
[IO.File]::WriteAllLines($OutputPath, $merged, [Text.UTF8Encoding]::new($false))
Write-Host "Merged input keys: $($initials.Count) dictionary entries"
