using System;
using System.Globalization;

namespace ActEnhanced.KeyboundProbe
{
    internal interface IKeyboundAfkMemory
    {
        int ProcessId { get; }
        long ModuleBase { get; }
        int ModuleSize { get; }
        string WriteFailure { get; }
        bool Read(long address, int count, out byte[] bytes);
        bool ZeroIdlePair(long address);
    }

    internal sealed class KeyboundAfkObservation
    {
        public string State = "inactive";
        public string Reason = String.Empty;
        public int ProcessId;
        public long UiModule;
        public long Director;
        public long AgentModule;
        public long Hud;
        public long Input;
        public long HudVtable;
        public long InputVtable;
        public string AgentTables = String.Empty;
        public float BeforeA;
        public float BeforeB;
        public float AfterA;
        public float AfterB;
        public bool Reset;
        public bool Failed { get { return State == "blocked" || State == "write-failed" || State == "readback-failed"; } }

        public string Detail()
        {
            return "state=" + State + "; reason=" + Reason + "; pid=" + ProcessId +
                "; ui=0x" + UiModule.ToString("X") + "; director=0x" + Director.ToString("X") +
                "; agentModule=0x" + AgentModule.ToString("X") + "; hud=0x" + Hud.ToString("X") +
                "; input=0x" + Input.ToString("X") +
                "; hudVtable=0x" + HudVtable.ToString("X") +
                "; inputVtable=0x" + InputVtable.ToString("X") + "; agentTables=" + AgentTables +
                "; before=" + Number(BeforeA) + "," + Number(BeforeB) +
                "; after=" + Number(AfterA) + "," + Number(AfterB);
        }
        private static string Number(float value) { return value.ToString("0.000", CultureInfo.InvariantCulture); }
    }

    // Independently implemented from the publicly documented agent relationship and idle offsets.
    // No in-process hooks: the only write is the validated input agent's two float idle values.
    internal static class KeyboundAfkGuard
    {
        internal const int HudAgentId = 397;
        internal const int InputAgentId = 398;
        internal const int IdleOffset = 0x50;
        internal const int IdlePairBytes = 8;
        private const int UiInputTimerOffset = 0xA9108;

        internal static bool DecodeAgentModuleOffset(byte[] code, out int offset)
        {
            offset = 0;
            // UIModule::GetAgentModule (virtual 37): lea rax,[rcx+disp32]; ret.
            if (code == null || code.Length != 8 || code[0] != 0x48 || code[1] != 0x8D ||
                code[2] != 0x81 || code[7] != 0xC3) return false;
            int decoded = BitConverter.ToInt32(code, 3);
            if (decoded < 0x1000 || decoded > 0x200000 || (decoded & 7) != 0) return false;
            offset = decoded;
            return true;
        }

        internal static bool ValidIdle(float value)
        {
            return !Single.IsNaN(value) && !Single.IsInfinity(value) && value >= 0 && value <= 86400;
        }

        internal static KeyboundAfkObservation Refresh(IKeyboundAfkMemory memory, long frameworkGlobal,
            long eventGlobal, bool enabled, DateTime activityUtc, DateTime now, Func<bool> canWrite)
        {
            var result = new KeyboundAfkObservation { ProcessId = memory.ProcessId };
            try
            {
                long framework = Pointer(memory, frameworkGlobal);
                result.UiModule = Pointer(memory, framework + 0x2B68);
                ushort territory = BitConverter.ToUInt16(Bytes(memory, result.UiModule + UiInputTimerOffset + 0x42, 2), 0);
                if (territory != 1359) { result.Reason = "outside-keybound"; return result; }
                result.Director = Pointer(memory, Pointer(memory, eventGlobal) + 0x158);
                if (Bytes(memory, result.Director + 0xDCE, 1)[0] != 23 ||
                    Bytes(memory, result.Director + 0x1FC0, 1)[0] == 0)
                { result.Reason = "match-not-underway"; return result; }
                long uiVtable = ModulePointer(memory, result.UiModule);
                long getter = ModulePointer(memory, uiVtable + 37 * 8);
                int offset;
                if (!DecodeAgentModuleOffset(Bytes(memory, getter, 8), out offset))
                    throw new InvalidOperationException("agent-getter-layout-mismatch");
                result.AgentModule = result.UiModule + offset;
                if (Pointer(memory, result.AgentModule + 8) != result.UiModule ||
                    Bytes(memory, result.AgentModule + 0x10, 1)[0] != 1)
                    throw new InvalidOperationException("agent-module-owner-or-init-mismatch");
                result.Hud = Pointer(memory, result.AgentModule + 0x20 + HudAgentId * 8);
                result.Input = Pointer(memory, result.AgentModule + 0x20 + InputAgentId * 8);
                result.HudVtable = AgentVtable(memory, result.Hud, result.UiModule);
                result.InputVtable = AgentVtable(memory, result.Input, result.UiModule);
                result.AgentTables = (InModule(memory, result.HudVtable) ? "module" : "relocated") + "/" +
                    (InModule(memory, result.InputVtable) ? "module" : "relocated");
                if (result.Hud == result.Input || Pointer(memory, result.Input + 0x28) != result.Hud)
                    throw new InvalidOperationException("agent-hud-link-mismatch");
                byte[] before = Bytes(memory, result.Input + IdleOffset, IdlePairBytes);
                result.BeforeA = BitConverter.ToSingle(before, 0);
                result.BeforeB = BitConverter.ToSingle(before, 4);
                result.AfterA = result.BeforeA;
                result.AfterB = result.BeforeB;
                if (!ValidIdle(result.BeforeA) || !ValidIdle(result.BeforeB))
                    throw new InvalidOperationException("invalid-idle-values");
                result.State = "observed";
                if (!enabled) { result.Reason = "automation-disabled"; return result; }
                double age = (now - activityUtc).TotalSeconds;
                if (activityUtc == DateTime.MinValue || age < 0 || age >= 30)
                { result.Reason = "no-recent-acknowledged-input"; return result; }

                // Revalidate volatile ownership and territory immediately before the single bounded write.
                if (Pointer(memory, frameworkGlobal) != framework || Pointer(memory, framework + 0x2B68) != result.UiModule ||
                    Pointer(memory, Pointer(memory, eventGlobal) + 0x158) != result.Director ||
                    BitConverter.ToUInt16(Bytes(memory, result.UiModule + UiInputTimerOffset + 0x42, 2), 0) != 1359 ||
                    Bytes(memory, result.Director + 0xDCE, 1)[0] != 23 || Bytes(memory, result.Director + 0x1FC0, 1)[0] == 0 ||
                    Pointer(memory, result.AgentModule + 0x20 + InputAgentId * 8) != result.Input ||
                    Pointer(memory, result.AgentModule + 0x20 + HudAgentId * 8) != result.Hud ||
                    Pointer(memory, result.AgentModule + 8) != result.UiModule ||
                    Bytes(memory, result.AgentModule + 0x10, 1)[0] != 1 ||
                    AgentVtable(memory, result.Hud, result.UiModule) != result.HudVtable ||
                    AgentVtable(memory, result.Input, result.UiModule) != result.InputVtable ||
                    Pointer(memory, result.Input + 0x28) != result.Hud)
                    throw new InvalidOperationException("state-changed-before-write");
                if (canWrite == null || !canWrite()) { result.Reason = "cancelled-before-write"; return result; }
                if (!memory.ZeroIdlePair(result.Input + IdleOffset))
                { result.State = "write-failed"; result.Reason = "write-denied-or-partial:" + memory.WriteFailure; return result; }
                byte[] after = Bytes(memory, result.Input + IdleOffset, IdlePairBytes);
                result.AfterA = BitConverter.ToSingle(after, 0);
                result.AfterB = BitConverter.ToSingle(after, 4);
                // The game may advance a frame between write and readback.
                if (!ValidIdle(result.AfterA) || !ValidIdle(result.AfterB) ||
                    result.AfterA > 1 || result.AfterB > 1)
                { result.State = "readback-failed"; result.Reason = "idle-not-reset"; return result; }
                result.Reset = true;
                result.State = "reset";
                result.Reason = "agent-idle-pair";
            }
            catch (Exception ex)
            {
                result.State = "blocked";
                result.Reason = ex.Message;
            }
            return result;
        }

        private static byte[] Bytes(IKeyboundAfkMemory memory, long address, int size)
        {
            byte[] bytes;
            if (address < 0x10000 || !memory.Read(address, size, out bytes) || bytes == null || bytes.Length != size)
                throw new InvalidOperationException("memory-unreadable");
            return bytes;
        }
        private static long Pointer(IKeyboundAfkMemory memory, long address)
        {
            long value = BitConverter.ToInt64(Bytes(memory, address, 8), 0);
            if (value < 0x10000 || value >= 0x0000800000000000L)
                throw new InvalidOperationException("pointer-invalid");
            return value;
        }
        private static long ModulePointer(IKeyboundAfkMemory memory, long address)
        {
            long value = Pointer(memory, address);
            if (!InModule(memory, value))
                throw new InvalidOperationException("vtable-or-code-outside-game-module");
            return value;
        }

        private static bool InModule(IKeyboundAfkMemory memory, long value)
        { return value >= memory.ModuleBase && value < memory.ModuleBase + memory.ModuleSize; }

        private static long AgentVtable(IKeyboundAfkMemory memory, long agent, long uiModule)
        {
            long table = Pointer(memory, agent);
            if (Pointer(memory, agent + 0x10) != uiModule)
                throw new InvalidOperationException("agent-ui-owner-mismatch");
            if (!InModule(memory, table))
            {
                // Live CN agents may use relocated tables with redirected methods. Their table address
                // alone is not evidence of an invalid agent. Require the observed unchanged slots as
                // well as UI ownership; all module/agent/HUD checks still run before the bounded write.
                foreach (int slot in new[] { 2, 4, 6 })
                {
                    long method = Pointer(memory, table + slot * 8);
                    if (!InModule(memory, method))
                        throw new InvalidOperationException("relocated-agent-method-outside-game-module:slot=" + slot);
                }
            }
            return table;
        }
    }
}
