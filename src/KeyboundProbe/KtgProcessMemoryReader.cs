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
    }

    internal sealed class KtgInputState
    {
        public bool Available;
        public bool Visible;
        public bool Focused;
        public string Text = String.Empty;
    }

    internal sealed class KtgProcessMemoryReader : IDisposable
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

        private static readonly byte[] AtkStagePattern =
        {
            0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x4C, 0x8B, 0x40, 0x18, 0x45, 0x8B, 0x40, 0x18
        };
        private static readonly bool[] AtkStagePatternMask =
        {
            true, true, true, false, false, false, false, true, true, true, true, true, true, true, true
        };

        private IntPtr processHandle;
        private int processId;
        private long moduleBase;
        private int moduleSize;
        private long atkStageGlobalAddress;
        private bool disposed;
        private readonly object memoryLock = new object();
        private long inputComponentAddress;
        private long inputRootNodeAddress;
        private DateTime lastInputRefreshUtc = DateTime.MinValue;

        public KtgMemorySnapshot ReadSnapshot()
        {
            lock (memoryLock)
                return ReadSnapshotUnlocked();
        }

        private KtgMemorySnapshot ReadSnapshotUnlocked()
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
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ResetProcess();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress,
            [Out] byte[] buffer, int size, out IntPtr numberOfBytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
