using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ActEnhanced.KeyboundProbe
{
    internal sealed class KeyboundHotkeyBinding
    {
        public int KeyCode;
        public int Modifiers;

        public KeyboundHotkeyBinding(int keyCode, int modifiers)
        {
            KeyCode = keyCode;
            Modifiers = modifiers & 15;
        }

        public bool SameAs(KeyboundHotkeyBinding other)
        {
            return other != null && KeyCode == other.KeyCode && Modifiers == other.Modifiers;
        }

        public string DisplayText
        {
            get
            {
                var parts = new List<string>();
                if ((Modifiers & KeyboundModeHotkeyManager.ModifierControl) != 0) parts.Add("Ctrl");
                if ((Modifiers & KeyboundModeHotkeyManager.ModifierAlt) != 0) parts.Add("Alt");
                if ((Modifiers & KeyboundModeHotkeyManager.ModifierShift) != 0) parts.Add("Shift");
                if ((Modifiers & KeyboundModeHotkeyManager.ModifierWin) != 0) parts.Add("Win");
                parts.Add(new KeysConverter().ConvertToString((Keys)KeyCode));
                return String.Join("+", parts.ToArray());
            }
        }
    }

    internal sealed class KeyboundModeHotkeyManager : NativeWindow, IDisposable
    {
        internal const int ModifierAlt = 0x0001;
        internal const int ModifierControl = 0x0002;
        internal const int ModifierShift = 0x0004;
        internal const int ModifierWin = 0x0008;
        private const int ModifierNoRepeat = 0x4000;
        private const int WindowHotkey = 0x0312;
        private const int HumanId = 0x4B10;
        private const int WarriorId = 0x4B11;
        private const int CustomId = 0x4B12;

        private bool disposed;
        private readonly HashSet<int> registeredIds = new HashSet<int>();

        public event Action<string> ModeRequested;

        public KeyboundModeHotkeyManager()
        {
            CreateHandle(new CreateParams());
        }

        public string Register(KeyboundHotkeyBinding human, KeyboundHotkeyBinding warrior,
            KeyboundHotkeyBinding custom)
        {
            UnregisterAll();
            if (disposed || Handle == IntPtr.Zero) return "热键窗口不可用";
            var failures = new List<string>();
            TryRegister(HumanId, human, "拟人", failures);
            TryRegister(WarriorId, warrior, "光之战士", failures);
            TryRegister(CustomId, custom, "自定义", failures);
            return failures.Count == 0 ? String.Empty : String.Join("；", failures.ToArray());
        }

        public void UnregisterAll()
        {
            if (Handle == IntPtr.Zero) return;
            foreach (int id in registeredIds)
                UnregisterHotKey(Handle, id);
            registeredIds.Clear();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WindowHotkey)
            {
                string mode = null;
                int id = message.WParam.ToInt32();
                if (id == HumanId) mode = KeyboundActSettings.HumanMode;
                else if (id == WarriorId) mode = KeyboundActSettings.WarriorMode;
                else if (id == CustomId) mode = KeyboundActSettings.CustomMode;
                Action<string> handler = ModeRequested;
                if (mode != null && handler != null) handler(mode);
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            UnregisterAll();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        private void TryRegister(int id, KeyboundHotkeyBinding binding, string label,
            ICollection<string> failures)
        {
            if (binding == null || binding.KeyCode <= 0 ||
                !RegisterHotKey(Handle, id, binding.Modifiers | ModifierNoRepeat, binding.KeyCode))
            {
                failures.Add(label + "热键注册失败");
                return;
            }
            registeredIds.Add(id);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }

    internal sealed class KeyboundHotkeyCaptureDialog : Form
    {
        public KeyboundHotkeyBinding Binding { get; private set; }

        public KeyboundHotkeyCaptureDialog(string modeLabel, KeyboundHotkeyBinding current)
        {
            Text = "设置" + modeLabel + "热键";
            Width = 360;
            Height = 145;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            KeyPreview = true;
            Controls.Add(new Label
            {
                AutoSize = true,
                Left = 18,
                Top = 18,
                Text = "请按下新的按键或组合键；Esc 取消。\r\n当前：" + current.DisplayText
            });
            KeyDown += OnCaptureKeyDown;
        }

        private void OnCaptureKeyDown(object sender, KeyEventArgs args)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            if (args.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            if (IsModifierOnly(args.KeyCode)) return;
            int modifiers = 0;
            if (args.Control) modifiers |= KeyboundModeHotkeyManager.ModifierControl;
            if (args.Alt) modifiers |= KeyboundModeHotkeyManager.ModifierAlt;
            if (args.Shift) modifiers |= KeyboundModeHotkeyManager.ModifierShift;
            Binding = new KeyboundHotkeyBinding((int)args.KeyCode, modifiers);
            DialogResult = DialogResult.OK;
            Close();
        }

        private static bool IsModifierOnly(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.LWin || key == Keys.RWin;
        }
    }
}
