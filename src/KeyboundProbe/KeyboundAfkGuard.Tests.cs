using System;
using System.Collections.Generic;
using ActEnhanced.KeyboundProbe;

internal static class KeyboundAfkGuardTests
{
    private const long FrameworkGlobal = 0x101000, EventGlobal = 0x101008;
    private const long Framework = 0x300000, Ui = 0x400000, Event = 0x500000;
    private const long Director = 0x600000, Hud = 0x700000, Input = 0x800000;
    private const int AgentOffset = 0xE4AB8;
    private static readonly DateTime Now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    internal static void Run(Action<bool, string> check)
    {
        int offset;
        check(KeyboundAfkGuard.DecodeAgentModuleOffset(new byte[] { 0x48, 0x8D, 0x81,
            0xB8, 0x4A, 0x0E, 0, 0xC3 }, out offset) && offset == AgentOffset,
            "AFK decodes locally observed CN getter");
        check(!KeyboundAfkGuard.DecodeAgentModuleOffset(new byte[] { 0x48, 0x8D, 0x81,
            0xB9, 0x4A, 0x0E, 0, 0xC3 }, out offset), "AFK rejects unaligned getter");
        var memory = new Memory();
        KeyboundAfkObservation result = Refresh(memory, true, Now.AddSeconds(-1), delegate { return true; });
        check(result.Reset && result.BeforeA == 75 && result.BeforeB == 74 && result.AfterA == 0 &&
            result.AfterB == 0 && memory.Writes == 1 && memory.LastWrite == Input + 0x50,
            "AFK resets exactly the validated specialized timer pair");
        check(memory.Data[Input + 0x4F] == 0xAA && memory.Data[Input + 0x58] == 0xBB,
            "AFK reset preserves neighboring bytes");

        memory = new Memory();
        memory.RelocateAgents();
        result = Refresh(memory, true, Now, delegate { return true; });
        check(result.Reset && memory.Writes == 1 && result.AgentTables == "relocated/relocated" &&
            result.HudVtable == 0x900000 && result.InputVtable == 0x910000,
            "AFK accepts observed relocated CN agent vtables with module methods and UI ownership");
        check(result.Detail().Contains("agentTables=relocated/relocated"),
            "AFK diagnostics identify relocated vtables");
        memory = new Memory();
        memory.RelocateAgents();
        result = Refresh(memory, false, Now, delegate { return true; });
        check(!result.Failed && result.State == "observed" && memory.Writes == 0,
            "AFK relocated tables remain observable while disabled");
        foreach (int slot in new[] { 2, 4, 6 })
        {
            memory = new Memory(); memory.RelocateAgents();
            memory.Pointer(0x910000 + slot * 8, 0xA00000);
            result = Refresh(memory, true, Now, delegate { return true; });
            check(result.Failed && memory.Writes == 0 && result.Reason.Contains("slot=" + slot),
                "AFK rejects relocated tables with unverified method slots");
        }
        memory = new Memory(); memory.RelocateAgents();
        memory.Pointer(Input + 0x10, Framework);
        result = Refresh(memory, true, Now, delegate { return true; });
        check(result.Failed && memory.Writes == 0 && result.Reason == "agent-ui-owner-mismatch",
            "AFK rejects relocated tables belonging to another UI");
        memory = new Memory(); memory.RelocateAgents();
        memory.BeforeRead = delegate(long address) {
            if (address == Input + 0x50) memory.Pointer(0x910000 + 4 * 8, 0xA00000);
        };
        result = Refresh(memory, true, Now, delegate { return true; });
        check(result.Failed && memory.Writes == 0,
            "AFK revalidates relocated table methods immediately before writing");

        memory = new Memory();
        result = Refresh(memory, false, Now, delegate { return true; });
        check(!result.Reset && !result.Failed && memory.Writes == 0 && result.BeforeA == 75,
            "AFK disabled mode observes without writing");
        foreach (DateTime activity in new[] { DateTime.MinValue, Now.AddSeconds(-30), Now.AddSeconds(1) })
        {
            memory = new Memory();
            result = Refresh(memory, true, activity, delegate { return true; });
            check(!result.Reset && memory.Writes == 0 && result.Reason == "no-recent-acknowledged-input",
                "AFK rejects missing, expired, or future activity");
        }
        memory = new Memory();
        result = Refresh(memory, true, Now, delegate { return false; });
        check(memory.Writes == 0 && result.Reason == "cancelled-before-write", "AFK honors stop cancellation");

        memory = new Memory();
        memory.Put(Ui + 0xA9108 + 0x42, BitConverter.GetBytes((ushort)1360));
        result = Refresh(memory, true, Now, delegate { return true; });
        check(memory.Writes == 0 && result.State == "inactive", "AFK never writes in registration area");
        memory = new Memory();
        memory.Put(Director + 0x1FC0, new byte[] { 0 });
        result = Refresh(memory, true, Now, delegate { return true; });
        check(memory.Writes == 0 && result.State == "inactive", "AFK never writes after match ends");

        foreach (Action<Memory> corrupt in new Action<Memory>[] {
            delegate(Memory m) { m.Put(0x110000, new byte[] { 0x90 }); },
            delegate(Memory m) { m.Pointer(Ui + AgentOffset + 8, Framework); },
            delegate(Memory m) { m.Put(Ui + AgentOffset + 0x10, new byte[] { 0 }); },
            delegate(Memory m) { m.Pointer(Input + 0x28, Framework); },
            delegate(Memory m) { m.Pointer(Input, 0x900000); },
            delegate(Memory m) { m.Pointer(Ui + AgentOffset + 0x20 + 398 * 8, Hud); },
            delegate(Memory m) { m.Data.Remove(Input + 0x50); }
        })
        {
            memory = new Memory();
            corrupt(memory);
            result = Refresh(memory, true, Now, delegate { return true; });
            check(result.Failed && memory.Writes == 0, "AFK rejects invalid layout, owner, link, or unreadable data");
        }
        foreach (float invalid in new[] { Single.NaN, Single.PositiveInfinity, -1f, 86401f })
        {
            memory = new Memory();
            memory.Put(Input + 0x54, BitConverter.GetBytes(invalid));
            result = Refresh(memory, true, Now, delegate { return true; });
            check(result.Failed && memory.Writes == 0, "AFK rejects invalid timer values");
        }
        memory = new Memory();
        memory.BeforeRead = delegate(long address) {
            if (address == Input + 0x50) memory.Pointer(Input + 0x28, Framework);
        };
        result = Refresh(memory, true, Now, delegate { return true; });
        check(memory.Writes == 0 && result.Reason == "state-changed-before-write",
            "AFK revalidates agent ownership immediately before write");
        memory = new Memory();
        memory.BeforeRead = delegate(long address) {
            if (address == Input + 0x50) memory.Put(Director + 0x1FC0, new byte[] { 0 });
        };
        result = Refresh(memory, true, Now, delegate { return true; });
        check(memory.Writes == 0 && result.Failed, "AFK aborts if match ends during observation");
        memory = new Memory { DenyWrite = true };
        result = Refresh(memory, true, Now, delegate { return true; });
        check(result.State == "write-failed" && !result.Reset && result.Reason.Contains("Win32=5"),
            "AFK reports denied or partial write including native error");
        memory = new Memory { IgnoreWrite = true };
        result = Refresh(memory, true, Now, delegate { return true; });
        check(result.State == "readback-failed" && !result.Reset, "AFK does not claim success without readback");
        memory = new Memory { FrameAdvance = true };
        result = Refresh(memory, true, Now, delegate { return true; });
        check(result.Reset && result.AfterA > 0 && result.AfterA < 1, "AFK tolerates one game frame before readback");
    }

    private static KeyboundAfkObservation Refresh(Memory memory, bool enabled, DateTime activity, Func<bool> gate)
    { return KeyboundAfkGuard.Refresh(memory, FrameworkGlobal, EventGlobal, enabled, activity, Now, gate); }

    private sealed class Memory : IKeyboundAfkMemory
    {
        internal readonly Dictionary<long, byte> Data = new Dictionary<long, byte>();
        internal int Writes;
        internal long LastWrite;
        internal bool DenyWrite, IgnoreWrite, FrameAdvance;
        internal Action<long> BeforeRead;
        public int ProcessId { get { return 42; } }
        public long ModuleBase { get { return 0x100000; } }
        public int ModuleSize { get { return 0x100000; } }
        public string WriteFailure { get { return "Win32=5"; } }
        internal Memory()
        {
            Pointer(FrameworkGlobal, Framework); Pointer(EventGlobal, Event);
            Pointer(Framework + 0x2B68, Ui); Pointer(Event + 0x158, Director);
            Put(Ui + 0xA9108 + 0x42, BitConverter.GetBytes((ushort)1359));
            Put(Director + 0xDCE, new byte[] { 23 }); Put(Director + 0x1FC0, new byte[] { 1 });
            Pointer(Ui, 0x120000); Pointer(0x120000 + 37 * 8, 0x110000);
            Put(0x110000, new byte[] { 0x48, 0x8D, 0x81, 0xB8, 0x4A, 0x0E, 0, 0xC3 });
            Pointer(Ui + AgentOffset + 8, Ui); Put(Ui + AgentOffset + 0x10, new byte[] { 1 });
            Pointer(Ui + AgentOffset + 0x20 + 397 * 8, Hud);
            Pointer(Ui + AgentOffset + 0x20 + 398 * 8, Input);
            Pointer(Hud, 0x121000); Pointer(Input, 0x122000); Pointer(Input + 0x28, Hud);
            Pointer(Hud + 0x10, Ui); Pointer(Input + 0x10, Ui);
            Put(Input + 0x50, BitConverter.GetBytes(75f)); Put(Input + 0x54, BitConverter.GetBytes(74f));
            Put(Input + 0x4F, new byte[] { 0xAA }); Put(Input + 0x58, new byte[] { 0xBB });
        }
        internal void Pointer(long address, long value) { Put(address, BitConverter.GetBytes(value)); }
        internal void RelocateAgents()
        {
            Pointer(Hud, 0x900000); Pointer(Input, 0x910000);
            foreach (long table in new[] { 0x900000L, 0x910000L })
                for (int slot = 0; slot < 8; slot++)
                    Pointer(table + slot * 8, slot == 2 || slot == 4 || slot == 6 ? 0x123000 : 0xA00000);
        }
        internal void Put(long address, byte[] value)
        { for (int i = 0; i < value.Length; i++) Data[address + i] = value[i]; }
        public bool Read(long address, int count, out byte[] bytes)
        {
            if (BeforeRead != null) BeforeRead(address);
            bytes = new byte[count];
            for (int i = 0; i < count; i++) if (!Data.TryGetValue(address + i, out bytes[i])) return false;
            return true;
        }
        public bool ZeroIdlePair(long address)
        {
            Writes++; LastWrite = address;
            if (DenyWrite) return false;
            if (!IgnoreWrite)
            {
                Put(address, new byte[8]);
                if (FrameAdvance) Put(address, BitConverter.GetBytes(0.016f));
            }
            return true;
        }
    }
}
