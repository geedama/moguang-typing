using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ActEnhanced.KeyboundProbe
{
    internal sealed class KtgMemoryTarget
    {
        public uint EntityId;
        public int AddonIndex;
        public int Slot;
        public int State;
        public bool Obstructed;
        public bool Visible;
        public string Text;
    }

    internal sealed class KtgMemorySnapshot
    {
        public int ProcessId;
        public long AtkStageAddress;
        public int AddonCount;
        public int HiddenTargetCount;
        public string Status;
        public bool InputVisible;
        public bool InputFocused;
        public string InputText = String.Empty;
        public string InputDiagnostic = "KTGInput addon not found";
        public readonly List<KtgMemoryTarget> Targets = new List<KtgMemoryTarget>();
        public KtgAfkTimerSnapshot AfkTimer;
        public KeyboundAfkObservation AfkGuard = null;
        public readonly List<KtgAddonProbe> Addons = new List<KtgAddonProbe>();
        public readonly List<KtgObjectProbe> Objects = new List<KtgObjectProbe>();
        public KtgObjectProbe LocalPlayer;
    }

    internal sealed class KtgAfkTimerSnapshot
    {
        public long Address;
        public float AfkTimer;
        public float ContentInputTimer;
        public float InputTimer;
        public float AutoAfkTimeLimit;
        public float InstanceContentAfkTimeLimit;
        public float PvpAfkTimeLimit;
        public float MjiAfkTimeLimit;
        public float AfkTimeLimit;
        public int Status;
        public ushort TerritoryTypeId;
        public readonly float[] ControllerTimers = new float[16];
        public readonly float[] MouseTimers = new float[5];
    }

    internal sealed class KtgAddonProbe
    {
        public long Address;
        public string Name = String.Empty;
        public bool Ready;
        public bool Visible;
        public ushort NodeCount;
        public readonly List<string> TextNodes = new List<string>();
        public readonly List<KtgButtonProbe> Buttons = new List<KtgButtonProbe>();
    }

    internal sealed class KtgButtonProbe
    {
        public uint NodeId;
        public string Text = String.Empty;
        public bool Enabled;
        public float X;
        public float Y;
    }

    internal sealed class KtgObjectProbe
    {
        public long Address;
        public uint EntityId;
        public uint LayoutId;
        public uint BaseId;
        public ushort ObjectIndex;
        public byte ObjectKind;
        public byte SubKind;
        public bool Targetable;
        public float X;
        public float Y;
        public float Z;
        public bool CurrentTarget;
        public string Name = String.Empty;
    }

    internal sealed class KtgInputState
    {
        public int ProcessId;
        public bool Available;
        public bool Visible;
        public bool Focused;
        public string Text = String.Empty;
    }

    internal sealed class KtgProcessMemoryReader : IDisposable, IKeyboundAfkMemory
    {
        internal const int KtgeWordEntriesOffset = 0x238;
        internal const int KtgeWordEntrySize = 0xC8;
        internal const int KtgeWordEntryCount = 20;
        internal const int KtgeWordTextNodeOffset = 0x70;
        internal const int KtgeWordEntityIdOffset = 0xA8;
        internal const int KtgeWordStateOffset = 0xB0;
        internal const int KtgeWordObstructedOffset = 0xB4;
        internal const int AtkUnitBaseUldManagerOffset = 0x28;
        internal const int AtkUldManagerNodeListCountOffset = 0x42;
        internal const int AtkUldManagerNodeListOffset = 0x50;
        internal const int AtkResNodeTypeOffset = 0x40;
        internal const int AtkResNodeIdOffset = 0x8;
        internal const int AtkComponentNodeComponentOffset = 0xC0;
        internal const int AtkComponentNodeTypeMinimum = 1000;
        internal const int KtgInputNodeId = 7;
        internal const int AtkComponentBaseOwnerNodeOffset = 0xA8;
        internal const int AtkComponentInputBaseIsActiveOffset = 0xC0;
        internal const int AtkComponentInputBaseRawStringOffset = 0x148;
        internal const int AtkComponentInputBaseOwnerAddonOffset = 0x1B0;
        internal const int InputTimerAfkTimerOffset = 0x10;
        internal const int InputTimerContentInputTimerOffset = 0x14;
        internal const int InputTimerInputTimerOffset = 0x18;
        internal const int InputTimerAutoAfkTimeLimitOffset = 0x20;
        internal const int InputTimerInstanceContentAfkTimeLimitOffset = 0x28;
        internal const int InputTimerPvpAfkTimeLimitOffset = 0x2C;
        internal const int InputTimerMjiAfkTimeLimitOffset = 0x30;
        internal const int InputTimerAfkTimeLimitOffset = 0x38;
        internal const int InputTimerStatusOffset = 0x3C;
        internal const int InputTimerTerritoryTypeIdOffset = 0x42;

        private const int ProcessVmRead = 0x0010;
        private const int ProcessQueryInformation = 0x0400;
        private const long RaptureAtkUnitManagerOffset = 0x20;
        private const long AllLoadedUnitsListOffset = 0x6900;
        private const long AtkUnitListEntriesOffset = 0x8;
        private const long AtkUnitListCountOffset = 0x808;
        private const long AtkUnitBaseNameOffset = 0x8;
        private const long AtkUnitBaseFlags1A1Offset = 0x1A1;
        private const long AtkResNodeFlagsOffset = 0xAE;
        private const long AtkTextNodeTextOffset = 0xD0;
        private const int NodeVisibleFlag = 0x10;
        private const uint InvalidEntityId = 0xE0000000;
        private const long FrameworkUIModuleOffset = 0x2B68;
        private const long UIModuleInputTimerModuleOffset = 0xA9108;
        private const long TargetSystemCurrentTargetOffset = 0x80;

        private static readonly byte[] AtkStagePattern =
        {
            0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x4C, 0x8B, 0x40, 0x18, 0x45, 0x8B, 0x40, 0x18
        };
        private static readonly bool[] AtkStagePatternMask =
        {
            true, true, true, false, false, false, false, true, true, true, true, true, true, true, true
        };

        private static readonly byte[] FrameworkPattern =
        {
            0x48, 0x8B, 0x1D, 0, 0, 0, 0, 0x8B, 0x7C, 0x24
        };
        private static readonly bool[] FrameworkPatternMask =
        {
            true, true, true, false, false, false, false, true, true, true
        };
        private static readonly byte[] ObjectManagerPattern =
        {
            0x48, 0x8D, 0x35, 0, 0, 0, 0, 0x81, 0xFA
        };
        private static readonly bool[] ObjectManagerPatternMask =
        {
            true, true, true, false, false, false, false, true, true
        };
        private static readonly byte[] TargetSystemPattern =
        {
            0x48, 0x8D, 0x0D, 0, 0, 0, 0, 0xE8, 0, 0, 0, 0, 0x48, 0x3B, 0xC6, 0x0F, 0x95, 0xC0
        };
        private static readonly bool[] TargetSystemPatternMask =
        {
            true, true, true, false, false, false, false, true, false, false, false, false,
            true, true, true, true, true, true
        };

        private IntPtr processHandle;
        private int processId;
        private long moduleBase;
        private int moduleSize;
        private long atkStageGlobalAddress;
        private long frameworkGlobalAddress;
        private long eventFrameworkGlobalAddress;
        private DateTime lastAcknowledgedInputUtc = DateTime.MinValue;
        private string afkWriteFailure = String.Empty;
        private long objectManagerAddress;
        private long targetSystemAddress;
        private bool disposed;
        private readonly object memoryLock = new object();
        private long inputComponentAddress;
        private long inputRootNodeAddress;
        private DateTime lastInputRefreshUtc = DateTime.MinValue;

        public KtgMemorySnapshot ReadSnapshot()
        {
            return ReadSnapshot(false);
        }

        public KtgMemorySnapshot ReadSnapshot(bool includeProbe)
        {
            lock (memoryLock)
                return ReadSnapshotUnlocked(includeProbe);
        }

        public void RecordAcknowledgedInput(int expectedProcessId, Func<bool> current)
        {
            lock (memoryLock)
            {
                if (!disposed && expectedProcessId != 0 && processId == expectedProcessId &&
                    current != null && current()) lastAcknowledgedInputUtc = DateTime.UtcNow;
            }
        }

        public KeyboundAfkObservation RefreshAfkGuard(bool enabled, Func<bool> canWrite)
        {
            lock (memoryLock)
            {
                if (disposed || processHandle == IntPtr.Zero)
                    return new KeyboundAfkObservation { Reason = "no-game-process" };
                if (frameworkGlobalAddress == 0)
                    frameworkGlobalAddress = ResolveRipAddress(FrameworkPattern, FrameworkPatternMask);
                if (eventFrameworkGlobalAddress == 0)
                {
                    byte[] pattern = { 0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x48, 0x85, 0xC0,
                        0x74, 0, 0x83, 0xB8, 0, 0, 0, 0, 0, 0x7C };
                    bool[] mask = { true, true, true, false, false, false, false, true, true, true,
                        true, false, true, true, false, false, false, false, false, true };
                    eventFrameworkGlobalAddress = ResolveRipAddress(pattern, mask);
                }
                KeyboundAfkObservation result = KeyboundAfkGuard.Refresh(this, frameworkGlobalAddress, eventFrameworkGlobalAddress,
                    enabled, lastAcknowledgedInputUtc, DateTime.UtcNow, canWrite);
                if (result.State == "inactive") lastAcknowledgedInputUtc = DateTime.MinValue;
                return result;
            }
        }

        int IKeyboundAfkMemory.ProcessId { get { return processId; } }
        long IKeyboundAfkMemory.ModuleBase { get { return moduleBase; } }
        int IKeyboundAfkMemory.ModuleSize { get { return moduleSize; } }
        string IKeyboundAfkMemory.WriteFailure { get { return afkWriteFailure; } }
        bool IKeyboundAfkMemory.Read(long address, int count, out byte[] bytes)
        { return ReadBytes(address, count, out bytes); }
        bool IKeyboundAfkMemory.ZeroIdlePair(long address)
        {
            // All normal reads retain the read-only handle. The write handle is scoped to eight bytes.
            afkWriteFailure = String.Empty;
            IntPtr writer = OpenProcess(0x0020 | 0x0008 | ProcessQueryInformation, false, processId);
            if (writer == IntPtr.Zero)
            { afkWriteFailure = "OpenProcess-Win32=" + Marshal.GetLastWin32Error(); return false; }
            try
            {
                long originalCreated, writerCreated, exited, kernel, user;
                if (!GetProcessTimes(processHandle, out originalCreated, out exited, out kernel, out user) ||
                    !GetProcessTimes(writer, out writerCreated, out exited, out kernel, out user) ||
                    originalCreated != writerCreated)
                { afkWriteFailure = "process-identity-unverified"; return false; }
                IntPtr written;
                bool success = WriteProcessMemory(writer, new IntPtr(address), new byte[KeyboundAfkGuard.IdlePairBytes],
                    KeyboundAfkGuard.IdlePairBytes, out written);
                if (!success || written.ToInt64() != KeyboundAfkGuard.IdlePairBytes)
                { afkWriteFailure = "WriteProcessMemory-Win32=" + Marshal.GetLastWin32Error() +
                    "; bytes=" + written.ToInt64(); return false; }
                return true;
            }
            finally { CloseHandle(writer); }
        }

        private KtgMemorySnapshot ReadSnapshotUnlocked(bool includeProbe = false)
        {
            var snapshot = new KtgMemorySnapshot();
            if (disposed)
            {
                snapshot.Status = "内存读取器已停止";
                return snapshot;
            }
            try
            {
                if (!EnsureProcess(snapshot))
                    return snapshot;
                snapshot.ProcessId = processId;
                if (includeProbe)
                    ReadDeepProbe(snapshot);

                if (atkStageGlobalAddress == 0)
                {
                    long match = ScanPattern(AtkStagePattern, AtkStagePatternMask);
                    if (match == 0)
                    {
                        snapshot.Status = "已连接 FF14，但未找到 AtkStage 特征码（可能需要更新偏移）";
                        return snapshot;
                    }
                    int displacement;
                    if (!ReadInt32(match + 3, out displacement))
                    {
                        snapshot.Status = "找到 AtkStage 特征码，但无法读取 RIP 位移";
                        return snapshot;
                    }
                    atkStageGlobalAddress = match + 7 + displacement;
                }

                long atkStage;
                if (!ReadPointer(atkStageGlobalAddress, out atkStage) || !IsPointer(atkStage))
                {
                    snapshot.Status = "AtkStage 尚未就绪";
                    return snapshot;
                }
                snapshot.AtkStageAddress = atkStage;

                long unitManager;
                if (!ReadPointer(atkStage + RaptureAtkUnitManagerOffset, out unitManager) ||
                    !IsPointer(unitManager))
                {
                    snapshot.Status = "RaptureAtkUnitManager 尚未就绪";
                    return snapshot;
                }

                long list = unitManager + AllLoadedUnitsListOffset;
                ushort count;
                if (!ReadUInt16(list + AtkUnitListCountOffset, out count))
                {
                    snapshot.Status = "无法读取 AtkUnitList";
                    return snapshot;
                }
                int unitCount = Math.Min(256, (int)count);
                inputComponentAddress = 0;
                inputRootNodeAddress = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int addonIndex = 0;
                for (int index = 0; index < unitCount; index++)
                {
                    long addon;
                    if (!ReadPointer(list + AtkUnitListEntriesOffset + index * 8L, out addon) ||
                        !IsPointer(addon))
                        continue;
                    string name = ReadFixedAscii(addon + AtkUnitBaseNameOffset, 32);
                    if (includeProbe)
                        ReadAddonProbe(addon, name, snapshot);
                    if (String.Equals(name, "KTGInput", StringComparison.Ordinal))
                    {
                        ReadInputAddon(addon, snapshot);
                        continue;
                    }
                    if (!String.Equals(name, "KTGEWord", StringComparison.Ordinal))
                        continue;
                    byte readyFlags;
                    if (!ReadByte(addon + AtkUnitBaseFlags1A1Offset, out readyFlags) ||
                        (readyFlags & 1) == 0)
                        continue;

                    int currentAddonIndex = addonIndex++;
                    snapshot.AddonCount++;
                    ReadAddonTargets(addon, currentAddonIndex, snapshot, seen);
                }
                snapshot.Status = "PID " + processId + "；AtkStage 0x" +
                    atkStage.ToString("X") + "；KTGEWord " + snapshot.AddonCount +
                    "；可见题目 " + snapshot.Targets.Count +
                    "；KTGInput " + (inputComponentAddress != 0
                        ? (snapshot.InputVisible ? "可见/" : "不可见/") +
                            (snapshot.InputFocused ? "已聚焦" : "未聚焦") + "[" + snapshot.InputText + "]"
                        : "不可用(" + snapshot.InputDiagnostic + ")") +
                    (snapshot.HiddenTargetCount == 0 ? String.Empty :
                    "；隐藏/无效 " + snapshot.HiddenTargetCount);
            }
            catch (Exception ex)
            {
                snapshot.Status = "内存读取失败：" + ex.GetType().Name + "：" + ex.Message;
                ResetProcess();
            }
            return snapshot;
        }

        private void ReadDeepProbe(KtgMemorySnapshot snapshot)
        {
            ReadAfkTimerProbe(snapshot);
            ReadObjectProbe(snapshot);
        }

        private void ReadAfkTimerProbe(KtgMemorySnapshot snapshot)
        {
            if (frameworkGlobalAddress == 0)
                frameworkGlobalAddress = ResolveRipAddress(FrameworkPattern, FrameworkPatternMask);
            long framework;
            long uiModule;
            if (frameworkGlobalAddress == 0 || !ReadPointer(frameworkGlobalAddress, out framework) ||
                !IsPointer(framework) || !ReadPointer(framework + FrameworkUIModuleOffset, out uiModule) ||
                !IsPointer(uiModule))
                return;
            long address = uiModule + UIModuleInputTimerModuleOffset;
            var timer = new KtgAfkTimerSnapshot { Address = address };
            if (!ReadSingle(address + InputTimerAfkTimerOffset, out timer.AfkTimer) ||
                !ReadSingle(address + InputTimerContentInputTimerOffset, out timer.ContentInputTimer) ||
                !ReadSingle(address + InputTimerInputTimerOffset, out timer.InputTimer) ||
                !ReadSingle(address + InputTimerAutoAfkTimeLimitOffset, out timer.AutoAfkTimeLimit) ||
                !ReadSingle(address + InputTimerInstanceContentAfkTimeLimitOffset, out timer.InstanceContentAfkTimeLimit) ||
                !ReadSingle(address + InputTimerPvpAfkTimeLimitOffset, out timer.PvpAfkTimeLimit) ||
                !ReadSingle(address + InputTimerMjiAfkTimeLimitOffset, out timer.MjiAfkTimeLimit) ||
                !ReadSingle(address + InputTimerAfkTimeLimitOffset, out timer.AfkTimeLimit) ||
                !ReadInt32(address + InputTimerStatusOffset, out timer.Status) ||
                !ReadUInt16(address + InputTimerTerritoryTypeIdOffset, out timer.TerritoryTypeId))
                return;
            for (int index = 0; index < timer.ControllerTimers.Length; index++)
                ReadSingle(address + 0x5C + index * 0x38L + 0x30, out timer.ControllerTimers[index]);
            for (int index = 0; index < timer.MouseTimers.Length; index++)
                ReadSingle(address + 0x3DC + index * 0x38L + 0x30, out timer.MouseTimers[index]);
            snapshot.AfkTimer = timer;
        }

        private void ReadObjectProbe(KtgMemorySnapshot snapshot)
        {
            if (objectManagerAddress == 0)
                objectManagerAddress = ResolveRipAddress(ObjectManagerPattern, ObjectManagerPatternMask);
            if (targetSystemAddress == 0)
                targetSystemAddress = ResolveRipAddress(TargetSystemPattern, TargetSystemPatternMask);
            if (!IsPointer(objectManagerAddress))
                return;
            long currentTarget = 0;
            if (IsPointer(targetSystemAddress))
                ReadPointer(targetSystemAddress + TargetSystemCurrentTargetOffset, out currentTarget);
            for (int index = 0; index < 819 && snapshot.Objects.Count < 160; index++)
            {
                long address;
                if (!ReadPointer(objectManagerAddress + 0x20 + index * 8L, out address) || !IsPointer(address))
                    continue;
                byte kind;
                if (!ReadByte(address + 0x90, out kind) || (kind == 1 && index != 0))
                    continue;
                string name = ReadFixedUtf8(address + 0x30, 64);
                if (String.IsNullOrWhiteSpace(name) && address != currentTarget)
                    continue;
                var value = new KtgObjectProbe { Address = address, Name = name, ObjectKind = kind,
                    CurrentTarget = address == currentTarget };
                ReadUInt32(address + 0x78, out value.EntityId);
                ReadUInt32(address + 0x7C, out value.LayoutId);
                ReadUInt32(address + 0x84, out value.BaseId);
                ReadUInt16(address + 0x8C, out value.ObjectIndex);
                ReadByte(address + 0x91, out value.SubKind);
                byte targetable;
                ReadByte(address + 0x9A, out targetable);
                value.Targetable = targetable != 0;
                if (!ReadSingle(address + 0xB0, out value.X) ||
                    !ReadSingle(address + 0xB4, out value.Y) ||
                    !ReadSingle(address + 0xB8, out value.Z)) continue;
                if (kind == 1 && index == 0)
                    snapshot.LocalPlayer = value;
                else
                    snapshot.Objects.Add(value);
            }
        }

        private void ReadAddonProbe(long addon, string name, KtgMemorySnapshot snapshot)
        {
            if (String.IsNullOrWhiteSpace(name) || snapshot.Addons.Count >= 256)
                return;
            byte readyFlags;
            long root;
            ushort count;
            var value = new KtgAddonProbe { Address = addon, Name = name };
            if (ReadByte(addon + AtkUnitBaseFlags1A1Offset, out readyFlags))
                value.Ready = (readyFlags & 1) != 0;
            if (ReadPointer(addon + 0xC8, out root) && IsPointer(root))
                value.Visible = IsNodeVisible(root);
            if (ReadUInt16(addon + AtkUnitBaseUldManagerOffset + AtkUldManagerNodeListCountOffset, out count))
                value.NodeCount = count;
            if (value.Ready && value.Visible &&
                name.IndexOf("Chat", StringComparison.OrdinalIgnoreCase) < 0)
            {
                long nodeList;
                if (ReadPointer(addon + AtkUnitBaseUldManagerOffset + AtkUldManagerNodeListOffset, out nodeList) &&
                    IsPointer(nodeList))
                {
                    int nodeCount = Math.Min(512, (int)value.NodeCount);
                    for (int index = 0; index < nodeCount; index++)
                    {
                        long node;
                        ushort type;
                        uint nodeId;
                        if (!ReadPointer(nodeList + index * 8L, out node) || !IsPointer(node) ||
                            !ReadUInt16(node + AtkResNodeTypeOffset, out type) ||
                            !ReadUInt32(node + AtkResNodeIdOffset, out nodeId))
                            continue;
                        if (type >= AtkComponentNodeTypeMinimum &&
                            (name == "KTGEnterDialogue" || name == "ContentsFinderConfirm" || name == "KTGResult"))
                            ReadFlowButton(node, nodeId, value);
                        if (type != 3 || value.TextNodes.Count >= 32) continue;
                        string text = ReadUtf8String(node + AtkTextNodeTextOffset);
                        if (!String.IsNullOrWhiteSpace(text))
                            value.TextNodes.Add(nodeId + "=" + text);
                    }
                }
            }
            snapshot.Addons.Add(value);
        }

        private void ReadFlowButton(long node, uint nodeId, KtgAddonProbe addon)
        {
            long component;
            long owner;
            long textNode;
            ushort textType;
            if (!ReadPointer(node + AtkComponentNodeComponentOffset, out component) || !IsPointer(component) ||
                !ReadPointer(component + AtkComponentBaseOwnerNodeOffset, out owner) || owner != node ||
                !ReadPointer(component + 0xC8, out textNode) || !IsPointer(textNode) ||
                !ReadUInt16(textNode + AtkResNodeTypeOffset, out textType) || textType != 3)
                return;
            string text = ReadUtf8String(textNode + AtkTextNodeTextOffset);
            if (String.IsNullOrWhiteSpace(text)) return;
            float x, y;
            ushort width, height, flags;
            if (!ReadSingle(node + 0x70, out x) || !ReadSingle(node + 0x74, out y) ||
                !ReadUInt16(node + 0xA0, out width) || !ReadUInt16(node + 0xA2, out height) ||
                !ReadUInt16(node + AtkResNodeFlagsOffset, out flags)) return;
            float scaleX = 1, scaleY = 1;
            var seen = new HashSet<long>();
            long parent = node;
            while (IsPointer(parent))
            {
                if (!seen.Add(parent) || seen.Count > 32 || !IsNodeVisible(parent)) return;
                float sx, sy;
                if (!ReadSingle(parent + 0x4C, out sx) || !ReadSingle(parent + 0x50, out sy)) return;
                scaleX *= sx;
                scaleY *= sy;
                if (!ReadPointer(parent + 0x20, out parent)) return;
            }
            float centerX = x + width * scaleX / 2;
            float centerY = y + height * scaleY / 2;
            if (Single.IsNaN(centerX) || Single.IsNaN(centerY) || Single.IsInfinity(centerX) ||
                Single.IsInfinity(centerY) || centerX < 0 || centerY < 0 || scaleX <= 0 || scaleY <= 0)
                return;
            addon.Buttons.Add(new KtgButtonProbe { NodeId = nodeId, Text = text,
                Enabled = (flags & 0x20) != 0, X = centerX, Y = centerY });
        }

        private long ResolveRipAddress(byte[] pattern, bool[] mask)
        {
            long match = ScanPattern(pattern, mask);
            int displacement;
            if (match == 0 || !ReadInt32(match + 3, out displacement))
                return 0;
            long resolved = match + 7 + displacement;
            return resolved;
        }

        public KtgInputState ReadInputState()
        {
            lock (memoryLock)
            {
                var state = new KtgInputState();
                if (disposed)
                    return state;
                if (inputComponentAddress == 0 &&
                    DateTime.UtcNow - lastInputRefreshUtc >= TimeSpan.FromMilliseconds(100))
                {
                    lastInputRefreshUtc = DateTime.UtcNow;
                    ReadSnapshotUnlocked();
                }
                if (inputComponentAddress == 0)
                    return state;
                byte active;
                if (!ReadByte(inputComponentAddress + AtkComponentInputBaseIsActiveOffset, out active))
                {
                    inputComponentAddress = 0;
                    inputRootNodeAddress = 0;
                    return state;
                }
                state.Available = true;
                state.ProcessId = processId;
                state.Visible = inputRootNodeAddress != 0 && IsNodeVisible(inputRootNodeAddress);
                state.Focused = active != 0;
                state.Text = ReadUtf8String(inputComponentAddress + AtkComponentInputBaseRawStringOffset);
                return state;
            }
        }

        private void ReadInputAddon(long addon, KtgMemorySnapshot snapshot)
        {
            long root;
            byte readyFlags;
            if (!ReadByte(addon + AtkUnitBaseFlags1A1Offset, out readyFlags) || (readyFlags & 1) == 0)
            {
                snapshot.InputDiagnostic = "addon not ready";
                return;
            }
            if (!ReadPointer(addon + 0xC8, out root) || !IsPointer(root))
            {
                snapshot.InputDiagnostic = "root node invalid";
                return;
            }
            snapshot.InputVisible = IsNodeVisible(root);
            ushort count;
            long nodeList;
            if (!ReadUInt16(addon + AtkUnitBaseUldManagerOffset + AtkUldManagerNodeListCountOffset, out count) ||
                !ReadPointer(addon + AtkUnitBaseUldManagerOffset + AtkUldManagerNodeListOffset, out nodeList) ||
                !IsPointer(nodeList))
            {
                snapshot.InputDiagnostic = "node list invalid";
                return;
            }
            snapshot.InputDiagnostic = "node 7 not found; count=" + count;
            int nodeCount = Math.Min(512, (int)count);
            for (int index = 0; index < nodeCount; index++)
            {
                long node;
                uint nodeId;
                ushort nodeType;
                if (!ReadPointer(nodeList + index * 8L, out node) || !IsPointer(node) ||
                    !ReadUInt32(node + AtkResNodeIdOffset, out nodeId) || nodeId != KtgInputNodeId ||
                    !ReadUInt16(node + AtkResNodeTypeOffset, out nodeType) ||
                    nodeType < AtkComponentNodeTypeMinimum)
                    continue;
                long component;
                if (!ReadPointer(node + AtkComponentNodeComponentOffset, out component) || !IsPointer(component))
                {
                    snapshot.InputDiagnostic = "node 7 component invalid; type=" + nodeType;
                    continue;
                }
                long ownerNode;
                long ownerAddon;
                if (!ReadPointer(component + AtkComponentBaseOwnerNodeOffset, out ownerNode) || ownerNode != node ||
                    !ReadPointer(component + AtkComponentInputBaseOwnerAddonOffset, out ownerAddon) ||
                    ownerAddon != addon)
                {
                    snapshot.InputDiagnostic = "node 7 owner mismatch; type=" + nodeType;
                    continue;
                }
                byte active;
                if (!ReadByte(component + AtkComponentInputBaseIsActiveOffset, out active))
                {
                    snapshot.InputDiagnostic = "node 7 input unreadable; type=" + nodeType;
                    continue;
                }
                inputComponentAddress = component;
                inputRootNodeAddress = root;
                snapshot.InputFocused = active != 0;
                snapshot.InputText = ReadUtf8String(component + AtkComponentInputBaseRawStringOffset);
                snapshot.InputDiagnostic = "ok; nodeType=" + nodeType + "; component=0x" +
                    component.ToString("X") + "; visible=" + snapshot.InputVisible;
                return;
            }
        }

        private void ReadAddonTargets(long addon, int addonIndex, KtgMemorySnapshot snapshot,
            HashSet<string> seen)
        {
            for (int slot = 0; slot < KtgeWordEntryCount; slot++)
            {
                long entry = addon + KtgeWordEntriesOffset + slot * KtgeWordEntrySize;
                long rootNode;
                long textNode;
                uint entityId;
                int state;
                byte obstructed;
                if (!ReadPointer(entry, out rootNode) || !ReadPointer(entry + KtgeWordTextNodeOffset, out textNode) ||
                    !ReadUInt32(entry + KtgeWordEntityIdOffset, out entityId) ||
                    !ReadInt32(entry + KtgeWordStateOffset, out state) ||
                    !ReadByte(entry + KtgeWordObstructedOffset, out obstructed))
                    continue;
                if (!IsPointer(rootNode) || !IsPointer(textNode) || entityId == 0 || entityId == InvalidEntityId)
                    continue;

                bool visible = IsNodeVisible(rootNode) && IsNodeVisible(textNode);
                string text = ReadUtf8String(textNode + AtkTextNodeTextOffset);
                if (String.IsNullOrWhiteSpace(text))
                {
                    snapshot.HiddenTargetCount++;
                    continue;
                }
                if (!visible)
                {
                    snapshot.HiddenTargetCount++;
                    continue;
                }
                string identity = entityId.ToString("X8") + ":" + text;
                if (!seen.Add(identity))
                    continue;
                snapshot.Targets.Add(new KtgMemoryTarget
                {
                    EntityId = entityId,
                    AddonIndex = addonIndex,
                    Slot = slot,
                    State = state,
                    Obstructed = obstructed != 0,
                    Visible = visible,
                    Text = text
                });
            }
        }

        private bool EnsureProcess(KtgMemorySnapshot snapshot)
        {
            if (processHandle != IntPtr.Zero)
            {
                try
                {
                    Process current = Process.GetProcessById(processId);
                    if (!current.HasExited)
                        return true;
                }
                catch { }
                ResetProcess();
            }

            Process selected = SelectFfxivProcess();
            if (selected == null)
            {
                snapshot.Status = "等待 ffxiv_dx11/ffxiv 进程";
                return false;
            }
            ProcessModule module = selected.MainModule;
            IntPtr handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, selected.Id);
            if (handle == IntPtr.Zero)
            {
                snapshot.Status = "无法以只读方式打开 FF14 进程，Win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            processHandle = handle;
            processId = selected.Id;
            moduleBase = module.BaseAddress.ToInt64();
            moduleSize = module.ModuleMemorySize;
            atkStageGlobalAddress = 0;
            inputComponentAddress = 0;
            inputRootNodeAddress = 0;
            lastInputRefreshUtc = DateTime.MinValue;
            return true;
        }

        private static Process SelectFfxivProcess()
        {
            var candidates = new List<Process>();
            candidates.AddRange(Process.GetProcessesByName("ffxiv_dx11"));
            candidates.AddRange(Process.GetProcessesByName("ffxiv"));
            Process selected = null;
            DateTime latest = DateTime.MinValue;
            foreach (Process candidate in candidates)
            {
                try
                {
                    if (!candidate.HasExited && candidate.StartTime >= latest)
                    {
                        selected = candidate;
                        latest = candidate.StartTime;
                    }
                }
                catch { }
            }
            return selected;
        }

        private long ScanPattern(byte[] pattern, bool[] mask)
        {
            const int chunkSize = 1024 * 1024;
            int overlap = pattern.Length - 1;
            byte[] carry = new byte[0];
            long offset = 0;
            while (offset < moduleSize)
            {
                int wanted = (int)Math.Min(chunkSize, moduleSize - offset);
                byte[] current;
                if (!ReadBytes(moduleBase + offset, wanted, out current) || current.Length == 0)
                {
                    carry = new byte[0];
                    offset += 0x1000;
                    continue;
                }
                byte[] combined = new byte[carry.Length + current.Length];
                Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
                Buffer.BlockCopy(current, 0, combined, carry.Length, current.Length);
                int found = FindPattern(combined, pattern, mask);
                if (found >= 0)
                    return moduleBase + offset - carry.Length + found;
                int keep = Math.Min(overlap, combined.Length);
                carry = new byte[keep];
                Buffer.BlockCopy(combined, combined.Length - keep, carry, 0, keep);
                offset += current.Length;
            }
            return 0;
        }

        internal static int FindPattern(byte[] data, byte[] pattern, bool[] mask)
        {
            if (data == null || pattern == null || mask == null || pattern.Length == 0 ||
                pattern.Length != mask.Length || data.Length < pattern.Length)
                return -1;
            for (int index = 0; index <= data.Length - pattern.Length; index++)
            {
                int part = 0;
                for (; part < pattern.Length; part++)
                    if (mask[part] && data[index + part] != pattern[part])
                        break;
                if (part == pattern.Length)
                    return index;
            }
            return -1;
        }

        private bool IsNodeVisible(long node)
        {
            byte flags;
            return ReadByte(node + AtkResNodeFlagsOffset, out flags) && (flags & NodeVisibleFlag) != 0;
        }

        private string ReadUtf8String(long utf8String)
        {
            long pointer;
            ulong used;
            if (!ReadPointer(utf8String, out pointer) || !ReadUInt64(utf8String + 0x10, out used) ||
                !IsPointer(pointer) || used <= 1 || used > 2048)
                return String.Empty;
            byte[] bytes;
            if (!ReadBytes(pointer, (int)used - 1, out bytes))
                return String.Empty;
            string value = Encoding.UTF8.GetString(bytes);
            var clean = new StringBuilder(value.Length);
            foreach (char character in value)
                if (!Char.IsControl(character) && character != '\uFFFD')
                    clean.Append(character);
            return clean.ToString().Trim();
        }

        private string ReadFixedAscii(long address, int length)
        {
            byte[] bytes;
            if (!ReadBytes(address, length, out bytes))
                return String.Empty;
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return Encoding.ASCII.GetString(bytes, 0, end);
        }

        private string ReadFixedUtf8(long address, int length)
        {
            byte[] bytes;
            if (!ReadBytes(address, length, out bytes))
                return String.Empty;
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            string value = Encoding.UTF8.GetString(bytes, 0, end);
            return value.Replace("\uFFFD", String.Empty).Trim();
        }

        private bool ReadPointer(long address, out long value)
        {
            ulong raw;
            bool result = ReadUInt64(address, out raw);
            value = unchecked((long)raw);
            return result;
        }

        private bool ReadByte(long address, out byte value)
        {
            byte[] bytes;
            bool result = ReadBytes(address, 1, out bytes);
            value = result ? bytes[0] : (byte)0;
            return result;
        }

        private bool ReadUInt16(long address, out ushort value)
        {
            byte[] bytes;
            bool result = ReadBytes(address, 2, out bytes);
            value = result ? BitConverter.ToUInt16(bytes, 0) : (ushort)0;
            return result;
        }

        private bool ReadInt32(long address, out int value)
        {
            byte[] bytes;
            bool result = ReadBytes(address, 4, out bytes);
            value = result ? BitConverter.ToInt32(bytes, 0) : 0;
            return result;
        }

        private bool ReadUInt32(long address, out uint value)
        {
            byte[] bytes;
            bool result = ReadBytes(address, 4, out bytes);
            value = result ? BitConverter.ToUInt32(bytes, 0) : 0;
            return result;
        }

        private bool ReadUInt64(long address, out ulong value)
        {
            byte[] bytes;
            bool result = ReadBytes(address, 8, out bytes);
            value = result ? BitConverter.ToUInt64(bytes, 0) : 0;
            return result;
        }

        private bool ReadSingle(long address, out float value)
        {
            byte[] bytes;
            bool result = ReadBytes(address, 4, out bytes);
            value = result ? BitConverter.ToSingle(bytes, 0) : 0;
            return result;
        }

        private bool ReadBytes(long address, int count, out byte[] bytes)
        {
            bytes = new byte[count];
            IntPtr read;
            if (processHandle == IntPtr.Zero || count <= 0 ||
                !ReadProcessMemory(processHandle, new IntPtr(address), bytes, count, out read) ||
                read.ToInt64() != count)
            {
                bytes = new byte[0];
                return false;
            }
            return true;
        }

        private static bool IsPointer(long value)
        {
            return value >= 0x10000 && value < 0x0000800000000000L;
        }

        private void ResetProcess()
        {
            if (processHandle != IntPtr.Zero)
                CloseHandle(processHandle);
            processHandle = IntPtr.Zero;
            processId = 0;
            moduleBase = 0;
            moduleSize = 0;
            atkStageGlobalAddress = 0;
            frameworkGlobalAddress = 0;
            eventFrameworkGlobalAddress = 0;
            lastAcknowledgedInputUtc = DateTime.MinValue;
            objectManagerAddress = 0;
            targetSystemAddress = 0;
            inputComponentAddress = 0;
            inputRootNodeAddress = 0;
        }

        public void Dispose()
        {
            lock (memoryLock)
            {
                if (disposed) return;
                disposed = true;
                ResetProcess();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress,
            byte[] buffer, int size, out IntPtr numberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit,
            out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress,
            [Out] byte[] buffer, int size, out IntPtr numberOfBytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
