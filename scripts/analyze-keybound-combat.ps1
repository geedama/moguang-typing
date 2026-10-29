param(
    [string]$ActRoot,
    [string]$OutputPath,
    [string]$ResolvedNamesPath
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ActRoot)) {
    $ActRoot = Join-Path $workspaceRoot 'NewMoe-Technology\ACT.DieMoe'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot 'artifacts\keybound-combat-analysis\combat-analysis.json'
}
if ([string]::IsNullOrWhiteSpace($ResolvedNamesPath)) {
    $ResolvedNamesPath = Join-Path $workspaceRoot 'artifacts\keybound-combat-analysis\resolved-names.tsv'
}

function Parse-AutoTime([string]$Line) {
    if ($Line.Length -lt 23) { return $null }
    $value = [DateTime]::MinValue
    if ([DateTime]::TryParseExact($Line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeLocal, [ref]$value)) { return $value }
    return $null
}

function Session-For([DateTime]$Time, [object[]]$Sessions) {
    foreach ($session in $Sessions) {
        if ($Time -ge $session.Start -and $Time -le $session.End) { return $session }
    }
    return $null
}

function Skill-Category([string]$Name) {
    if ($Name -match '干扰|妨碍|臭气|诅咒|针刺') { return '干扰与控制' }
    if ($Name -match '播种|出芽') { return '生成与召唤' }
    if ($Name -match '炸弹|榴弹|爆炸') { return '爆炸与范围伤害' }
    if ($Name -match '加农炮|导弹') { return '远程炮击' }
    return '未命名或基础动作'
}

$autoLog = Join-Path $ActRoot 'Plugins\KeyboundProbe\KeyboundProbe.auto.log'
if (-not (Test-Path -LiteralPath $autoLog)) { throw "Auto log not found: $autoLog" }

$resolvedNames = @{}
if (Test-Path -LiteralPath $ResolvedNamesPath) {
    foreach ($line in [IO.File]::ReadLines($ResolvedNamesPath)) {
        if ($line.StartsWith('#') -or [String]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Split("`t")
        if ($parts.Count -ge 3 -and -not [String]::IsNullOrWhiteSpace($parts[2])) {
            $resolvedNames[($parts[0].ToLowerInvariant() + ':' + $parts[1].ToUpperInvariant())] = $parts[2]
        }
    }
}

$phaseEvents = New-Object System.Collections.Generic.List[object]
$inputClosedTimes = New-Object System.Collections.Generic.List[DateTime]
$lastAutoTime = [DateTime]::MinValue
foreach ($line in [IO.File]::ReadLines($autoLog)) {
    $time = Parse-AutoTime $line
    if ($null -eq $time) { continue }
    if ($time -gt $lastAutoTime) { $lastAutoTime = $time }
    if ($line -match "`tMEMORY`t" -and $line -match 'KTGEWord 0') {
        $inputClosedTimes.Add($time)
    }
    if ($line -notmatch "`tPHASE`t") { continue }
    $parts = $line.Split("`t")
    if ($parts.Count -ge 5) {
        $phaseEvents.Add([pscustomobject]@{ Time = $time; Phase = $parts[4].Trim() })
    }
}

$sessions = New-Object System.Collections.Generic.List[object]
$sessionId = 0
for ($index = 0; $index -lt $phaseEvents.Count; $index++) {
    if ($phaseEvents[$index].Phase -ne '0x1') { continue }
    $start = $phaseEvents[$index].Time
    $end = [DateTime]::MinValue
    $phases = New-Object System.Collections.Generic.List[string]
    for ($next = $index; $next -lt $phaseEvents.Count; $next++) {
        $event = $phaseEvents[$next]
        if ($event.Time -lt $start) { continue }
        if ($next -gt $index -and $event.Phase -eq '0x1') { break }
        if (-not $phases.Contains($event.Phase)) { $phases.Add($event.Phase) }
        if ($event.Phase -eq '0x0' -or $event.Phase -eq '0x4' -or $event.Phase -eq '0x5') {
            $end = $event.Time
            break
        }
    }
    if ($end -eq [DateTime]::MinValue) {
        $end = $start.AddMinutes(10)
        if ($lastAutoTime -lt $end -and $lastAutoTime -gt $start) { $end = $lastAutoTime }
    }
    $closed = @($inputClosedTimes | Where-Object {
        $_ -ge $start.AddSeconds(5) -and $_ -lt $start.AddMinutes(10)
    } | Sort-Object | Select-Object -First 1)
    if ($closed.Count -ne 0 -and $closed[0] -lt $end) { $end = $closed[0] }
    if ($end -gt $start.AddMinutes(10)) { $end = $start.AddMinutes(10) }
    $sessionId++
    $sessions.Add([pscustomobject]@{
        Id = $sessionId
        Start = $start
        End = $end
        DurationSeconds = [Math]::Round(($end - $start).TotalSeconds, 1)
        Phases = @($phases)
    })
}
if ($sessions.Count -eq 0) { throw 'No Keybound sessions were identified from PHASE events.' }

$promptBySessionEntity = @{}
$allPromptTexts = New-Object System.Collections.Generic.List[string]
foreach ($line in [IO.File]::ReadLines($autoLog)) {
    if ($line -notmatch "`tBOARD`t") { continue }
    $time = Parse-AutoTime $line
    if ($null -eq $time) { continue }
    $session = Session-For $time $sessions
    if ($null -eq $session) { continue }
    foreach ($match in [regex]::Matches($line, '(?<![0-9A-F])([0-9A-F]{8}):(\d+):([^|]+)\|')) {
        $entity = $match.Groups[1].Value.ToUpperInvariant()
        $text = $match.Groups[3].Value
        $key = "$($session.Id):$entity"
        if (-not $promptBySessionEntity.ContainsKey($key)) {
            $promptBySessionEntity[$key] = New-Object System.Collections.Generic.HashSet[string]
        }
        [void]$promptBySessionEntity[$key].Add($text)
        $allPromptTexts.Add($text)
    }
}

$actors = @{}
$actorNames = @{}
$skills = @{}
$statuses = @{}
$networkFiles = New-Object System.Collections.Generic.List[string]
$sessionDates = @($sessions | ForEach-Object { $_.Start.ToString('yyyyMMdd') } | Sort-Object -Unique)
foreach ($date in $sessionDates) {
    $networkFile = Join-Path $ActRoot "FFXIVLogs\Network_*_$date.log"
    foreach ($path in @(Get-ChildItem -Path $networkFile -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)) {
        $networkFiles.Add($path)
        foreach ($line in [IO.File]::ReadLines($path)) {
            $parts = $line.Split('|')
            if ($parts.Count -lt 4) { continue }
            $timestamp = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParse($parts[1], [ref]$timestamp)) { continue }
            $localTime = $timestamp.LocalDateTime
            $session = Session-For $localTime $sessions
            $type = $parts[0]
            if ($null -eq $session -and ($type -eq '261' -or $type -eq '03')) {
                foreach ($candidate in $sessions) {
                    if ($localTime -ge $candidate.Start.AddSeconds(-60) -and
                        $localTime -lt $candidate.Start) {
                        $session = $candidate
                        break
                    }
                }
            }
            if ($null -eq $session) { continue }
            if ($type -eq '261' -and $parts.Count -ge 6 -and $parts[2] -eq 'Add') {
                $entity = $parts[3].ToUpperInvariant()
                if (-not $entity.StartsWith('4')) { continue }
                $values = @{}
                for ($item = 4; $item + 1 -lt $parts.Count - 1; $item += 2) {
                    $values[$parts[$item]] = $parts[$item + 1]
                }
                $key = "$($session.Id):$entity"
                $actors[$key] = [pscustomobject]@{
                    SessionId = $session.Id; EntityId = $entity
                    BNpcId = [string]$values['BNpcID']; BNpcNameId = [string]$values['BNpcNameID']
                    MaxHp = if ($values['MaxHP']) { [int64]$values['MaxHP'] } else { 0 }
                    Level = if ($values['Level']) { [int]$values['Level'] } else { 0 }
                    Type = [string]$values['Type']
                }
                continue
            }
            if ($type -eq '03' -and $parts.Count -ge 4) {
                $entity = $parts[2].ToUpperInvariant()
                if ($entity.StartsWith('4') -and -not [String]::IsNullOrWhiteSpace($parts[3])) {
                    $actorNames["$($session.Id):$entity"] = $parts[3]
                }
                continue
            }
            if (($type -eq '20' -or $type -eq '21' -or $type -eq '22') -and $parts.Count -ge 7) {
                $sourceId = $parts[2].ToUpperInvariant()
                if (-not $sourceId.StartsWith('4')) { continue }
                $actionId = $parts[4].ToUpperInvariant()
                $actionName = $parts[5]
                $resolved = $resolvedNames['action:' + $actionId]
                if ([String]::IsNullOrWhiteSpace($actionName) -or $actionName -like 'unknown_*') {
                    $actionName = if ($resolved) { $resolved } else { "未命名技能 $actionId" }
                }
                $skillKey = $actionId
                if (-not $skills.ContainsKey($skillKey)) {
                    $skills[$skillKey] = [pscustomobject]@{
                        ActionId = $actionId; Name = $actionName
                        CastCount = 0; ExecuteCount = 0; AoeCount = 0
                        First = $localTime; Last = $localTime
                        SourceKeys = New-Object System.Collections.Generic.HashSet[string]
                    }
                }
                $skill = $skills[$skillKey]
                if ($type -eq '20') { $skill.CastCount++ } else { $skill.ExecuteCount++ }
                if ($type -eq '22') { $skill.AoeCount++ }
                if ($localTime -lt $skill.First) { $skill.First = $localTime }
                if ($localTime -gt $skill.Last) { $skill.Last = $localTime }
                [void]$skill.SourceKeys.Add("$($session.Id):$sourceId")
                if (-not [String]::IsNullOrWhiteSpace($parts[3])) {
                    $actorNames["$($session.Id):$sourceId"] = $parts[3]
                }
                continue
            }
            if (($type -eq '26' -or $type -eq '30') -and $parts.Count -ge 9) {
                $effectId = $parts[2].ToUpperInvariant()
                $effectName = $parts[3]
                if ([String]::IsNullOrWhiteSpace($effectName)) { $effectName = "未命名状态 $effectId" }
                if (-not $statuses.ContainsKey($effectId)) {
                    $statuses[$effectId] = [pscustomobject]@{
                        EffectId = $effectId; Name = $effectName; Adds = 0; Removes = 0
                        Durations = New-Object System.Collections.Generic.HashSet[string]
                        Sources = New-Object System.Collections.Generic.HashSet[string]
                        Targets = New-Object System.Collections.Generic.HashSet[string]
                        First = $localTime; Last = $localTime
                    }
                }
                $status = $statuses[$effectId]
                if ($type -eq '26') {
                    $status.Adds++
                    [void]$status.Durations.Add($parts[4])
                }
                else { $status.Removes++ }
                if ($parts[5] -and $parts[5] -ne 'E0000000') {
                    [void]$status.Sources.Add(($parts[6] ? $parts[6] : $parts[5]))
                }
                if ($parts[7]) { [void]$status.Targets.Add(($parts[8] ? $parts[8] : $parts[7])) }
                if ($localTime -lt $status.First) { $status.First = $localTime }
                if ($localTime -gt $status.Last) { $status.Last = $localTime }
            }
        }
    }
}

$kindGroups = @{}
foreach ($entry in $actors.GetEnumerator()) {
    $actor = $entry.Value
    $bnpcId = if ($actor.BNpcId) { $actor.BNpcId.ToUpperInvariant() } else { 'UNKNOWN' }
    $bnpcNameId = if ($actor.BNpcNameId) { $actor.BNpcNameId.ToUpperInvariant() } else { 'UNKNOWN' }
    $kindKey = "${bnpcId}:$bnpcNameId"
    if (-not $kindGroups.ContainsKey($kindKey)) {
        $resolvedName = $resolvedNames['bnpc:' + $bnpcNameId]
        $kindGroups[$kindKey] = [pscustomobject]@{
            BNpcId = $bnpcId; BNpcNameId = $bnpcNameId
            Name = if ($resolvedName) { $resolvedName } else { "类型 $bnpcId" }
            SpawnKeys = New-Object System.Collections.Generic.HashSet[string]
            MaxHpValues = New-Object System.Collections.Generic.HashSet[int64]
            Levels = New-Object System.Collections.Generic.HashSet[int]
            PromptTexts = New-Object System.Collections.Generic.HashSet[string]
            ObservedNames = New-Object System.Collections.Generic.HashSet[string]
            SkillIds = New-Object System.Collections.Generic.HashSet[string]
        }
    }
    $group = $kindGroups[$kindKey]
    [void]$group.SpawnKeys.Add($entry.Key)
    [void]$group.MaxHpValues.Add($actor.MaxHp)
    [void]$group.Levels.Add($actor.Level)
    if ($actorNames.ContainsKey($entry.Key)) { [void]$group.ObservedNames.Add($actorNames[$entry.Key]) }
    if ($promptBySessionEntity.ContainsKey($entry.Key)) {
        foreach ($text in $promptBySessionEntity[$entry.Key]) { [void]$group.PromptTexts.Add($text) }
    }
}
foreach ($skill in $skills.Values) {
    foreach ($sourceKey in $skill.SourceKeys) {
        if (-not $actors.ContainsKey($sourceKey)) { continue }
        $actor = $actors[$sourceKey]
        $kindKey = "$($actor.BNpcId.ToUpperInvariant()):$($actor.BNpcNameId.ToUpperInvariant())"
        if ($kindGroups.ContainsKey($kindKey)) { [void]$kindGroups[$kindKey].SkillIds.Add($skill.ActionId) }
    }
}

$kindRows = foreach ($group in $kindGroups.Values) {
    $maxHp = @($group.MaxHpValues | Sort-Object)
    $role = if (($maxHp | Measure-Object -Maximum).Maximum -ge 3000) { '首领或大型机制怪' }
        elseif (($maxHp | Measure-Object -Maximum).Maximum -ge 700) { '强化小怪' }
        elseif (($maxHp | Measure-Object -Maximum).Maximum -ge 400) { '标准小怪' }
        else { '轻型小怪' }
    $observedNames = @($group.ObservedNames | Sort-Object)
    [pscustomobject]@{
        BNpcId = $group.BNpcId; BNpcNameId = $group.BNpcNameId
        Name = if ($observedNames.Count -gt 0) { $observedNames -join ' / ' } else { $group.Name }
        Role = $role; Spawns = $group.SpawnKeys.Count
        MaxHp = @($maxHp); Levels = @($group.Levels | Sort-Object)
        PromptCount = $group.PromptTexts.Count
        PromptExamples = @($group.PromptTexts | Sort-Object { $_.Length } -Descending | Select-Object -First 5)
        SkillIds = @($group.SkillIds | Sort-Object)
    }
}

$skillRows = foreach ($skill in $skills.Values) {
    $sourceTypes = New-Object System.Collections.Generic.HashSet[string]
    foreach ($sourceKey in $skill.SourceKeys) {
        if ($actors.ContainsKey($sourceKey)) { [void]$sourceTypes.Add($actors[$sourceKey].BNpcId) }
        elseif ($actorNames.ContainsKey($sourceKey)) { [void]$sourceTypes.Add($actorNames[$sourceKey]) }
    }
    [pscustomobject]@{
        ActionId = $skill.ActionId; Name = $skill.Name
        Category = Skill-Category $skill.Name
        CastCount = $skill.CastCount; ExecuteCount = $skill.ExecuteCount; AoeCount = $skill.AoeCount
        SourceTypes = @($sourceTypes | Sort-Object)
        First = $skill.First.ToString('yyyy-MM-dd HH:mm:ss.fff')
        Last = $skill.Last.ToString('yyyy-MM-dd HH:mm:ss.fff')
    }
}

$statusRows = foreach ($status in $statuses.Values) {
    [pscustomobject]@{
        EffectId = $status.EffectId; Name = $status.Name
        Adds = $status.Adds; Removes = $status.Removes
        Durations = @($status.Durations | Sort-Object)
        Sources = @($status.Sources | Sort-Object)
        TargetCount = $status.Targets.Count
        First = $status.First.ToString('yyyy-MM-dd HH:mm:ss.fff')
        Last = $status.Last.ToString('yyyy-MM-dd HH:mm:ss.fff')
    }
}

$uniquePrompts = @($allPromptTexts | Sort-Object -Unique)
$longestPrompts = @($uniquePrompts | Sort-Object { $_.Length } -Descending | Select-Object -First 12 | ForEach-Object {
    [pscustomobject]@{ Text = $_; Length = $_.Length }
})
$result = [ordered]@{
    GeneratedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    Source = [ordered]@{
        AutoLog = $autoLog
        NetworkLogs = @($networkFiles)
        RangeStart = $sessions[0].Start.ToString('yyyy-MM-dd HH:mm:ss')
        RangeEnd = $sessions[$sessions.Count - 1].End.ToString('yyyy-MM-dd HH:mm:ss')
    }
    Summary = [ordered]@{
        Sessions = $sessions.Count
        SessionSeconds = [Math]::Round(($sessions | Measure-Object DurationSeconds -Sum).Sum, 1)
        CreatureKinds = $kindRows.Count
        CreatureSpawns = ($kindRows | Measure-Object Spawns -Sum).Sum
        Skills = $skillRows.Count
        CastSkills = @($skillRows | Where-Object CastCount -gt 0).Count
        Statuses = $statusRows.Count
        UniquePrompts = $uniquePrompts.Count
    }
    Sessions = @($sessions | ForEach-Object {
        [pscustomobject]@{
            Id = $_.Id; Start = $_.Start.ToString('yyyy-MM-dd HH:mm:ss')
            End = $_.End.ToString('yyyy-MM-dd HH:mm:ss')
            DurationSeconds = $_.DurationSeconds; Phases = $_.Phases
        }
    })
    CreatureKinds = @($kindRows | Sort-Object Spawns -Descending)
    Skills = @($skillRows | Sort-Object @{Expression='CastCount';Descending=$true}, @{Expression='ExecuteCount';Descending=$true})
    Statuses = @($statusRows | Sort-Object Adds -Descending)
    LongestPrompts = $longestPrompts
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$json = $result | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
Write-Host "Sessions: $($result.Summary.Sessions)"
Write-Host "Creature kinds/spawns: $($result.Summary.CreatureKinds)/$($result.Summary.CreatureSpawns)"
Write-Host "Skills/cast skills: $($result.Summary.Skills)/$($result.Summary.CastSkills)"
Write-Host "Statuses: $($result.Summary.Statuses)"
Write-Host "Unique prompts: $($result.Summary.UniquePrompts)"
Write-Host "Output: $OutputPath"
