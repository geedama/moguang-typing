-- Run in the administrator CE Lua environment attached to ffxiv_dx11.exe.
-- Reads target memory only. No breakpoints, hooks, allocations or target function calls.
-- Stop manually with: kbAfkProbe.stop('manual')
-- Add a manual-input timestamp with: kbAfkProbe.mark('manual-input-start')
if kbAfkProbe and kbAfkProbe.stop then kbAfkProbe.stop('replaced') end

local pid = getOpenedProcessID()
assert(pid and pid ~= 0 and getOpenedProcessHandle() ~= 0, 'Attach CE to FF14 first')
local function rip(signature)
    local match = AOBScanModuleUnique('ffxiv_dx11.exe', signature)
    assert(match, 'Signature unavailable: ' .. signature)
    local displacement = readInteger(match + 3)
    assert(displacement, 'Unreadable RIP displacement')
    if displacement >= 0x80000000 then displacement = displacement - 0x100000000 end
    return match + 7 + displacement
end
local frameworkGlobal = rip('48 8B 1D ?? ?? ?? ?? 8B 7C 24')
local eventGlobal = rip('48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ?? 83 B8 ?? ?? ?? ?? ?? 7C')
local stageGlobal = rip('48 8B 05 ?? ?? ?? ?? 4C 8B 40 18 45 8B 40 18')
local outputDirectory = 'E:/actEnhanced/NewMoe-Technology/ACT.DieMoe/Plugins/KeyboundProbe/Captures/'
local path = outputDirectory .. 'keybound-' .. os.date('%Y%m%d-%H%M%S') .. '-' .. getTickCount() .. '.ce-afk.tsv'
local file = assert(io.open(path, 'w'))
local started = getTickCount()
local probe = { path = path, active = true, samples = 0, bytes = 0, previous = {}, pid = pid }
kbAfkProbe = probe
local function elapsed() return (getTickCount() - started) % 4294967296 end
local function clean(value)
    local result = tostring(value or ''):gsub('[\t\r\n]', ' ')
    return result
end
local function log(kind, scope, address, offset, raw, value, detail)
    local line = table.concat({os.date('!%Y-%m-%dT%H:%M:%SZ'), tostring(elapsed()), kind,
        scope or '', string.format('0x%X', address or 0), string.format('0x%X', offset or 0),
        clean(raw), clean(value), clean(detail)}, '\t') .. '\n'
    file:write(line)
    probe.bytes = probe.bytes + #line
end
file:write('utc\tdelta_ms\tkind\tscope\taddress\toffset\traw_u32_hex\tfloat_value\tdetail\n')
function probe.stop(reason)
    if not probe.active then return end
    probe.active = false
    if probe.timer then probe.timer.Enabled = false; probe.timer.destroy(); probe.timer = nil end
    log('STOP', '', 0, 0, '', '', reason)
    file:flush()
    file:close()
end
function probe.mark(label)
    if probe.active then log('MARK', '', 0, 0, '', '', label); file:flush() end
end
local function block(scope, address, size)
    if not address or address < 0x10000 then return end
    local data = readBytes(address, size, true)
    if not data or #data ~= size then log('UNREADABLE', scope, address, 0); return end
    local old = probe.previous[scope]
    local baseline = not old or old.address ~= address
    for i = 1, size - 3, 4 do
        if baseline or data[i] ~= old.data[i] or data[i+1] ~= old.data[i+1] or
            data[i+2] ~= old.data[i+2] or data[i+3] ~= old.data[i+3] then
            local packed = string.char(data[i], data[i+1], data[i+2], data[i+3])
            local float = string.unpack('<f', packed)
            local uint = string.unpack('<I4', packed)
            log(baseline and 'BASELINE' or 'CHANGE', scope, address, i - 1,
                string.format('%08X', uint), string.format('%.9g', float), 'u32=' .. uint)
        end
    end
    probe.previous[scope] = { address = address, data = data }
end
local timerFields = {{0x10,'afk'},{0x14,'content'},{0x18,'input'},{0x1C,'unknown_1C'},
    {0x20,'autoLimit'},{0x28,'instanceLimit'},{0x38,'afkLimit'}}
local function sample()
    if getOpenedProcessID() ~= pid or getOpenedProcessHandle() == 0 then
        probe.stop('process-changed-or-detached'); return
    end
    if elapsed() >= 15 * 60 * 1000 or probe.bytes >= 64 * 1024 * 1024 then
        probe.stop('time-or-size-limit'); return
    end
    local framework = readQword(frameworkGlobal)
    local ui = framework and readQword(framework + 0x2B68)
    if not ui then probe.stop('framework-unavailable'); return end
    local timer = ui + 0xA9108
    local territory = readSmallInteger(timer + 0x42)
    log('SAMPLE', 'InputTimer', timer, 0, '', '', 'territory=' .. tostring(territory))
    if territory ~= 1359 then probe.stop('left-keybound'); return end
    for _, field in ipairs(timerFields) do
        log('TIMER', 'InputTimer', timer, field[1], '', readFloat(timer+field[1]), field[2])
    end
    block('InputTimer', timer, 0x4F8)
    local event = readQword(eventGlobal)
    local director = event and readQword(event + 0x158)
    if director and director > 0x10000 and readBytes(director+0xDCE,1) == 23 then
        -- Base InstanceContentDirector is 0x1F98. Extra subclass bytes are unverified candidates.
        block('KeyboundDirector-base', director, 0x1F98)
        block('KeyboundDirector-extra-unverified', director+0x1F98, 0x2068)
    else
        log('DIRECTOR_UNAVAILABLE', 'KeyboundDirector', director, 0)
    end
    local stage = readQword(stageGlobal)
    local manager = stage and readQword(stage + 0x20)
    local list = manager and manager + 0x6900
    local count = list and readSmallInteger(list + 0x808)
    for i = 0, math.min(count or 0, 256) - 1 do
        local addon = readQword(list+8+i*8)
        if addon and addon > 0x10000 and readString(addon+8,32) == 'KTGInput' then
            block('KTGInput', addon, 0x238)
            break
        end
    end
    probe.samples = probe.samples + 1
    file:flush()
end
log('META', '', 0, 0, '', '', 'pid='..pid..';intervalMs=500;maxMinutes=15;readOnly=true')
log('META', '', 0, 0, '', '', string.format('frameworkGlobal=0x%X;eventGlobal=0x%X;stageGlobal=0x%X',
    frameworkGlobal, eventGlobal, stageGlobal))
log('META', '', 0, 0, '', '', 'extra-unverified bytes may include unrelated allocations; not confirmed AFK fields')
local ok, err = pcall(sample)
if not ok then probe.stop('error: '..tostring(err)); error(err) end
if probe.active then
    local timer = createTimer(nil, false)
    probe.timer = timer
    timer.Interval = 500
    timer.OnTimer = function()
        local success, message = pcall(sample)
        if not success then probe.stop('error: '..tostring(message)) end
    end
    timer.Enabled = true
end
return path
