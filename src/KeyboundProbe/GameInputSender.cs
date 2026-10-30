using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ActEnhanced.KeyboundProbe
{
    public enum GameInputResult
    {
        Sent,
        EmptyText,
        FfxivNotForeground,
        NativeInputFailed,
        Cancelled,
        ClipboardUnavailable,
        CandidateMismatch
    }

    public sealed class GameInputVerification
    {
        public GameInputResult Result;
        public string ActualText;
    }

    internal static class PhysicalPinyinSchedule
    {
        internal const int MinimumIntervalMilliseconds = 45000;
        internal const int MaximumIntervalMilliseconds = 60000;

        internal static int NextIntervalMilliseconds(Random random)
        {
            if (random == null) throw new ArgumentNullException("random");
            return random.Next(MinimumIntervalMilliseconds,
                MaximumIntervalMilliseconds + 1);
        }
    }

    public static class GameInputSender
    {
        internal const int DirectTypingToSubmitMinimumMs = 1500;
        internal const int DirectTypingToSubmitBudgetMs = 4000;
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;
        private const uint KeyEventScanCode = 0x0008;
        private const uint WindowKeyDown = 0x0100;
        private const uint WindowKeyUp = 0x0101;
        private const uint WindowCharacter = 0x0102;

        public static GameInputResult SendUnicode(string text, int characterDelayMilliseconds)
        {
            return SendUnicode(text, characterDelayMilliseconds, null);
        }

        public static GameInputResult SendUnicode(string text, int characterDelayMilliseconds,
            Func<bool> continuePredicate)
        {
            if (String.IsNullOrEmpty(text))
                return GameInputResult.EmptyText;
            if (continuePredicate != null && !continuePredicate())
                return GameInputResult.Cancelled;
            if (!IsFfxivForeground())
                return GameInputResult.FfxivNotForeground;

            int delay = Math.Max(0, Math.Min(250, characterDelayMilliseconds));
            foreach (char value in text)
            {
                if (continuePredicate != null && !continuePredicate())
                    return GameInputResult.Cancelled;
                if (!IsFfxivForeground())
                    return GameInputResult.FfxivNotForeground;
                if (!SendCharacter(value))
                    return GameInputResult.NativeInputFailed;
                if (delay != 0)
                    Thread.Sleep(delay);
            }
            return GameInputResult.Sent;
        }

        public static GameInputResult SubmitEnter(Func<bool> continuePredicate)
        {
            if (!CanContinue(continuePredicate))
                return GameInputResult.Cancelled;
            if (!IsFfxivForeground())
                return GameInputResult.FfxivNotForeground;
            return SendPhysicalKey(0x0D) ? GameInputResult.Sent : GameInputResult.NativeInputFailed;
        }

        public static GameInputResult SendPhysicalVirtualKey(ushort virtualKey,
            Func<bool> continuePredicate)
        {
            if (!CanContinue(continuePredicate))
                return GameInputResult.Cancelled;
            if (!IsFfxivForeground())
                return GameInputResult.FfxivNotForeground;
            return SendPhysicalKey(virtualKey)
                ? GameInputResult.Sent
                : GameInputResult.NativeInputFailed;
        }

        public static GameInputResult SendPhysicalPinyinAttempt(string fullPinyin,
            int keyHoldMilliseconds, Func<bool> continuePredicate)
        {
            string keys = NormalizeFullPinyinKeys(fullPinyin);
            if (keys.Length == 0)
                return GameInputResult.EmptyText;
            int hold = Math.Max(8, Math.Min(35, keyHoldMilliseconds));
            foreach (char character in keys)
            {
                if (!CanContinue(continuePredicate))
                    return GameInputResult.Cancelled;
                if (!IsFfxivForeground())
                    return GameInputResult.FfxivNotForeground;
                ushort virtualKey = character == '\'' ? (ushort)0xDE :
                    (ushort)Char.ToUpperInvariant(character);
                if (!SendPhysicalHeldKey(virtualKey, hold))
                    return GameInputResult.NativeInputFailed;
            }
            if (!CanContinue(continuePredicate) || !SendPhysicalHeldKey(0x20, hold))
                return CanContinue(continuePredicate)
                    ? GameInputResult.NativeInputFailed : GameInputResult.Cancelled;
            Thread.Sleep(80);
            if (!CanContinue(continuePredicate))
                return GameInputResult.Cancelled;
            return SendPhysicalHeldKey(0x0D, Math.Max(20, hold))
                ? GameInputResult.Sent
                : GameInputResult.NativeInputFailed;
        }

        public static GameInputResult PostUnicodeToFfxiv(string text, int characterDelayMilliseconds,
            Func<bool> continuePredicate)
        {
            if (String.IsNullOrEmpty(text))
                return GameInputResult.EmptyText;
            IntPtr window = FindFfxivWindow();
            if (window == IntPtr.Zero)
                return GameInputResult.NativeInputFailed;
            int delay = Math.Max(0, Math.Min(250, characterDelayMilliseconds));
            foreach (char value in text)
            {
                if (!CanContinue(continuePredicate))
                    return GameInputResult.Cancelled;
                if (!PostMessage(window, WindowCharacter, new IntPtr(value), new IntPtr(1)))
                    return GameInputResult.NativeInputFailed;
                if (delay != 0)
                    Thread.Sleep(delay);
            }
            return GameInputResult.Sent;
        }

        public static GameInputResult WakeKeyboundInput(Func<bool> continuePredicate)
        {
            if (!CanContinue(continuePredicate))
                return GameInputResult.Cancelled;
            if (!IsFfxivForeground())
                return GameInputResult.FfxivNotForeground;
            if (!SendPhysicalKey(0x20))
                return GameInputResult.NativeInputFailed;
            Thread.Sleep(80);
            if (!CanContinue(continuePredicate))
                return GameInputResult.Cancelled;
            if (!SendPhysicalKey(0x08))
                return GameInputResult.NativeInputFailed;
            Thread.Sleep(80);
            return GameInputResult.Sent;
        }

        public static GameInputResult PostEnterToFfxiv(Func<bool> continuePredicate)
        {
            if (!CanContinue(continuePredicate))
                return GameInputResult.Cancelled;
            IntPtr window = FindFfxivWindow();
            if (window == IntPtr.Zero)
                return GameInputResult.NativeInputFailed;
            const int scanCode = 0x1C;
            IntPtr down = new IntPtr(1 | (scanCode << 16));
            IntPtr up = new IntPtr(unchecked((long)(1 | (scanCode << 16) | 0xC0000000)));
            return PostMessage(window, WindowKeyDown, new IntPtr(0x0D), down) &&
                PostMessage(window, WindowKeyUp, new IntPtr(0x0D), up)
                ? GameInputResult.Sent
                : GameInputResult.NativeInputFailed;
        }

        public static GameInputResult PostBackspaceToFfxiv(Func<bool> continuePredicate)
        {
            if (!CanContinue(continuePredicate))
                return GameInputResult.Cancelled;
            IntPtr window = FindFfxivWindow();
            if (window == IntPtr.Zero)
                return GameInputResult.NativeInputFailed;
            const int scanCode = 0x0E;
            IntPtr down = new IntPtr(1 | (scanCode << 16));
            IntPtr up = new IntPtr(unchecked((long)(1 | (scanCode << 16) | 0xC0000000)));
            return PostMessage(window, WindowKeyDown, new IntPtr(0x08), down) &&
                PostMessage(window, WindowKeyUp, new IntPtr(0x08), up)
                ? GameInputResult.Sent
                : GameInputResult.NativeInputFailed;
        }

        public static string DescribeFfxivInputTarget()
        {
            IntPtr gameWindow = FindFfxivWindow();
            IntPtr foreground = GetForegroundWindow();
            uint foregroundProcessId = 0;
            if (foreground != IntPtr.Zero)
                GetWindowThreadProcessId(foreground, out foregroundProcessId);
            string foregroundName = String.Empty;
            if (foregroundProcessId != 0)
            {
                try
                {
                    using (Process process = Process.GetProcessById((int)foregroundProcessId))
                        foregroundName = process.ProcessName;
                }
                catch { }
            }
            return "gameHwnd=0x" + gameWindow.ToInt64().ToString("X") +
                "; foregroundHwnd=0x" + foreground.ToInt64().ToString("X") +
                "; foregroundPid=" + foregroundProcessId +
                "; foreground=" + foregroundName;
        }

        public static GameInputResult SendPinyin(string pinyin, int characterDelayMilliseconds,
            Func<bool> continuePredicate)
        {
            string keys = NormalizePinyinKeys(pinyin);
            if (keys.Length == 0)
                return GameInputResult.EmptyText;
            if (continuePredicate != null && !continuePredicate())
                return GameInputResult.Cancelled;
            if (!IsFfxivForeground())
                return GameInputResult.FfxivNotForeground;

            int delay = Math.Max(0, Math.Min(250, characterDelayMilliseconds));
            foreach (char value in keys)
            {
                if (continuePredicate != null && !continuePredicate())
                    return GameInputResult.Cancelled;
                if (!IsFfxivForeground())
                    return GameInputResult.FfxivNotForeground;
                if (!SendPhysicalKey((ushort)Char.ToUpperInvariant(value)))
                    return GameInputResult.NativeInputFailed;
                if (delay != 0)
                    Thread.Sleep(delay);
            }
            if (continuePredicate != null && !continuePredicate())
                return GameInputResult.Cancelled;
            if (!IsFfxivForeground())
                return GameInputResult.FfxivNotForeground;
            if (!SendPhysicalKey(0x20))
                return GameInputResult.NativeInputFailed;
            Thread.Sleep(Math.Max(30, delay));
            if (continuePredicate != null && !continuePredicate())
                return GameInputResult.Cancelled;
            if (!IsFfxivForeground())
                return GameInputResult.FfxivNotForeground;
            return SendPhysicalKey(0x0D) ? GameInputResult.Sent : GameInputResult.NativeInputFailed;
        }

        public static GameInputVerification ComposeVerifyAndSubmit(string fullPinyin,
            string expectedDisplayText, int characterDelayMilliseconds, Func<bool> continuePredicate)
        {
            string keys = NormalizeFullPinyinKeys(fullPinyin);
            string expected = NormalizeChineseAnswer(expectedDisplayText);
            if (keys.Length == 0 || expected.Length == 0)
                return Verify(GameInputResult.EmptyText, String.Empty);
            if (!CanContinue(continuePredicate))
                return Verify(GameInputResult.Cancelled, String.Empty);
            if (!IsFfxivForeground())
                return Verify(GameInputResult.FfxivNotForeground, String.Empty);

            int delay = Math.Max(0, Math.Min(250, characterDelayMilliseconds));
            IDataObject previousClipboard = null;
            string actual = String.Empty;
            try
            {
                try { previousClipboard = Clipboard.GetDataObject(); }
                catch { }
                for (int candidate = 1; candidate <= 9; candidate++)
                {
                    if (!CanContinue(continuePredicate))
                        return Verify(GameInputResult.Cancelled, actual);
                    if (!IsFfxivForeground())
                        return Verify(GameInputResult.FfxivNotForeground, actual);
                    if (!ClearCurrentText() || !SendPhysicalText(keys, delay, continuePredicate))
                        return Verify(GameInputResult.NativeInputFailed, actual);
                    ushort selectKey = candidate == 1 ? (ushort)0x20 : (ushort)(0x30 + candidate);
                    if (!SendPhysicalKey(selectKey))
                        return Verify(GameInputResult.NativeInputFailed, actual);
                    Thread.Sleep(Math.Max(100, delay));

                    try { Clipboard.Clear(); }
                    catch { return Verify(GameInputResult.ClipboardUnavailable, actual); }
                    if (!SendPhysicalChord(0x11, (ushort)'A') ||
                        !SendPhysicalChord(0x11, (ushort)'C'))
                        return Verify(GameInputResult.NativeInputFailed, actual);
                    Thread.Sleep(100);
                    try
                    {
                        if (!Clipboard.ContainsText())
                            return Verify(GameInputResult.ClipboardUnavailable, actual);
                        actual = Clipboard.GetText();
                    }
                    catch { return Verify(GameInputResult.ClipboardUnavailable, actual); }

                    if (String.Equals(NormalizeChineseAnswer(actual), expected,
                        StringComparison.Ordinal))
                    {
                        if (!CanContinue(continuePredicate))
                            return Verify(GameInputResult.Cancelled, actual);
                        if (!IsFfxivForeground())
                            return Verify(GameInputResult.FfxivNotForeground, actual);
                        return Verify(SendPhysicalKey(0x0D)
                            ? GameInputResult.Sent
                            : GameInputResult.NativeInputFailed, actual);
                    }
                }
                return Verify(GameInputResult.CandidateMismatch, actual);
            }
            finally
            {
                if (previousClipboard != null)
                {
                    try { Clipboard.SetDataObject(previousClipboard, true); }
                    catch { }
                }
            }
        }

        public static bool IsFfxivProcessName(string processName)
        {
            return String.Equals(processName, "ffxiv_dx11", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(processName, "ffxiv", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeChineseAnswer(string displayText)
        {
            var result = new StringBuilder();
            foreach (char value in displayText ?? String.Empty)
            {
                if (value >= '\u4e00' && value <= '\u9fff')
                    result.Append(value);
            }
            return result.ToString();
        }

        public static string NormalizePinyinKeys(string inputText)
        {
            var result = new StringBuilder();
            foreach (char value in inputText ?? String.Empty)
            {
                if ((value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z'))
                    result.Append(Char.ToLowerInvariant(value));
                else if (value == 'ü' || value == 'Ü')
                    result.Append('v');
                else
                    return String.Empty;
            }
            return result.ToString();
        }

        public static string NormalizeFullPinyinKeys(string inputText)
        {
            var result = new StringBuilder();
            foreach (char value in inputText ?? String.Empty)
            {
                bool startsSyllable = (value >= 'A' && value <= 'Z') || value == 'Ü';
                if (startsSyllable && result.Length != 0)
                    result.Append('\'');
                if ((value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z'))
                    result.Append(Char.ToLowerInvariant(value));
                else if (value == 'ü' || value == 'Ü')
                    result.Append('v');
                else
                    return String.Empty;
            }
            return result.ToString();
        }

        public static int NativeInputSize
        {
            get { return Marshal.SizeOf(typeof(Input)); }
        }

        private static bool CanContinue(Func<bool> continuePredicate)
        {
            return continuePredicate == null || continuePredicate();
        }

        private static GameInputVerification Verify(GameInputResult result, string actual)
        {
            return new GameInputVerification { Result = result, ActualText = actual ?? String.Empty };
        }

        private static bool ClearCurrentText()
        {
            return SendPhysicalChord(0x11, (ushort)'A') && SendPhysicalKey(0x08);
        }

        private static bool SendPhysicalText(string text, int delay, Func<bool> continuePredicate)
        {
            foreach (char value in text)
            {
                if (!CanContinue(continuePredicate) || !IsFfxivForeground())
                    return false;
                ushort virtualKey;
                if (value >= 'a' && value <= 'z')
                    virtualKey = (ushort)Char.ToUpperInvariant(value);
                else if (value == '\'')
                    virtualKey = 0xDE;
                else
                    return false;
                if (!SendPhysicalKey(virtualKey))
                    return false;
                if (delay != 0)
                    Thread.Sleep(delay);
            }
            return true;
        }

        private static bool IsFfxivForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == 0)
                return false;
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    return IsFfxivProcessName(process.ProcessName);
            }
            catch { return false; }
        }

        private static IntPtr FindFfxivWindow()
        {
            foreach (string name in new[] { "ffxiv_dx11", "ffxiv" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.Refresh();
                        if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                            return process.MainWindowHandle;
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            return IntPtr.Zero;
        }

        private static bool SendCharacter(char value)
        {
            Input[] inputs = { CreateInput(value, 0), CreateInput(value, KeyEventKeyUp) };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) ==
                (uint)inputs.Length;
        }

        private static bool SendPhysicalKey(ushort virtualKey)
        {
            ushort scanCode = (ushort)MapVirtualKey(virtualKey, 0);
            if (scanCode == 0)
                return false;
            Input[] inputs =
            {
                CreatePhysicalInput(scanCode, 0),
                CreatePhysicalInput(scanCode, KeyEventKeyUp)
            };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) ==
                (uint)inputs.Length;
        }

        private static bool SendPhysicalHeldKey(ushort virtualKey, int holdMilliseconds)
        {
            ushort scanCode = (ushort)MapVirtualKey(virtualKey, 0);
            if (scanCode == 0)
                return false;
            Input[] inputs =
            {
                CreatePhysicalInput(scanCode, 0)
            };
            if (SendInput(1, inputs, Marshal.SizeOf(typeof(Input))) != 1)
                return false;
            Thread.Sleep(holdMilliseconds);
            inputs[0] = CreatePhysicalInput(scanCode, KeyEventKeyUp);
            bool sent = SendInput(1, inputs, Marshal.SizeOf(typeof(Input))) == 1;
            Thread.Sleep(3);
            return sent;
        }

        private static bool SendPhysicalChord(ushort modifier, ushort key)
        {
            ushort modifierScan = (ushort)MapVirtualKey(modifier, 0);
            ushort keyScan = (ushort)MapVirtualKey(key, 0);
            if (modifierScan == 0 || keyScan == 0)
                return false;
            Input[] inputs =
            {
                CreatePhysicalInput(modifierScan, 0),
                CreatePhysicalInput(keyScan, 0),
                CreatePhysicalInput(keyScan, KeyEventKeyUp),
                CreatePhysicalInput(modifierScan, KeyEventKeyUp)
            };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) ==
                (uint)inputs.Length;
        }

        private static Input CreatePhysicalInput(ushort scanCode, uint extraFlags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        ScanCode = scanCode,
                        Flags = KeyEventScanCode | extraFlags
                    }
                }
            };
        }

        private static Input CreateInput(char value, uint extraFlags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        ScanCode = value,
                        Flags = KeyEventUnicode | extraFlags
                    }
                }
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint Type; public InputUnion Union; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
