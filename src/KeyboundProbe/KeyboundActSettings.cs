using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ActEnhanced.KeyboundProbe
{
    public sealed class KeyboundActSettings
    {
        public bool AutoLaunchDalamud = true;
        public bool Humanize = true;
        public bool BlatantMode;
        public int StartDelayMinMs = 600;
        public int StartDelayMaxMs = 1500;
        public int CharacterDelayMs = 120;
        public int JitterPercent = 40;
        public int SubmitDelayMinMs = 200;
        public int SubmitDelayMaxMs = 500;
        public int TypingSubmitMinimumMs = GameInputSender.DirectTypingToSubmitMinimumMs;
        public int TypingSubmitBudgetMs = GameInputSender.DirectTypingToSubmitBudgetMs;
        public string TargetOrder = "slot_asc";

        public static KeyboundActSettings Load(string path)
        {
            var value = new KeyboundActSettings();
            if (!File.Exists(path))
                return value;
            try
            {
                var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    int separator = line.IndexOf('=');
                    if (separator <= 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    entries[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
                value.AutoLaunchDalamud = ReadBool(entries, "AutoLaunchDalamud", value.AutoLaunchDalamud);
                value.Humanize = ReadBool(entries, "Humanize", value.Humanize);
                value.BlatantMode = ReadBool(entries, "BlatantMode", value.BlatantMode);
                value.StartDelayMinMs = ReadInt(entries, "StartDelayMinMs", value.StartDelayMinMs, 0, 5000);
                value.StartDelayMaxMs = ReadInt(entries, "StartDelayMaxMs", value.StartDelayMaxMs, 0, 5000);
                value.CharacterDelayMs = ReadInt(entries, "CharacterDelayMs", value.CharacterDelayMs, 0, 500);
                value.JitterPercent = ReadInt(entries, "JitterPercent", value.JitterPercent, 0, 80);
                value.SubmitDelayMinMs = ReadInt(entries, "SubmitDelayMinMs", value.SubmitDelayMinMs, 0, 2000);
                value.SubmitDelayMaxMs = ReadInt(entries, "SubmitDelayMaxMs", value.SubmitDelayMaxMs, 0, 2000);
                value.TypingSubmitMinimumMs = ReadInt(entries, "TypingSubmitMinimumMs",
                    value.TypingSubmitMinimumMs, 0, 10000);
                value.TypingSubmitBudgetMs = ReadInt(entries, "TypingSubmitBudgetMs",
                    value.TypingSubmitBudgetMs, 250, 10000);
                string order;
                if (entries.TryGetValue("TargetOrder", out order) &&
                    (order == "slot_asc" || order == "slot_desc" || order == "shortest" || order == "longest"))
                    value.TargetOrder = order;
            }
            catch { }
            value.Normalize();
            return value;
        }

        public void Save(string path)
        {
            Normalize();
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(path, new[]
            {
                "# ActEnhanced Keybound settings",
                "AutoLaunchDalamud=" + AutoLaunchDalamud,
                "Humanize=" + Humanize,
                "BlatantMode=" + BlatantMode,
                "StartDelayMinMs=" + StartDelayMinMs.ToString(CultureInfo.InvariantCulture),
                "StartDelayMaxMs=" + StartDelayMaxMs.ToString(CultureInfo.InvariantCulture),
                "CharacterDelayMs=" + CharacterDelayMs.ToString(CultureInfo.InvariantCulture),
                "JitterPercent=" + JitterPercent.ToString(CultureInfo.InvariantCulture),
                "SubmitDelayMinMs=" + SubmitDelayMinMs.ToString(CultureInfo.InvariantCulture),
                "SubmitDelayMaxMs=" + SubmitDelayMaxMs.ToString(CultureInfo.InvariantCulture),
                "TypingSubmitMinimumMs=" + TypingSubmitMinimumMs.ToString(CultureInfo.InvariantCulture),
                "TypingSubmitBudgetMs=" + TypingSubmitBudgetMs.ToString(CultureInfo.InvariantCulture),
                "TargetOrder=" + TargetOrder
            });
        }

        public int NextEquivalentTypingDelay(string text, Random random)
        {
            Normalize();
            if (BlatantMode)
                return 0;

            int total = Humanize ? NextRange(random, StartDelayMinMs, StartDelayMaxMs) : StartDelayMinMs;
            int characters = String.IsNullOrEmpty(text) ? 0 : text.Length;
            for (int index = 0; index < characters; index++)
            {
                int delay = CharacterDelayMs;
                if (Humanize && JitterPercent != 0)
                {
                    int jitter = delay * JitterPercent / 100;
                    delay = NextRange(random, Math.Max(0, delay - jitter), delay + jitter);
                }
                total += delay;
                if (Humanize && random.Next(100) < 5)
                    total += random.Next(80, 241);
            }
            total += Humanize ? NextRange(random, SubmitDelayMinMs, SubmitDelayMaxMs) : SubmitDelayMinMs;
            return total;
        }

        internal void Normalize()
        {
            StartDelayMinMs = Clamp(StartDelayMinMs, 0, 5000);
            StartDelayMaxMs = Clamp(StartDelayMaxMs, StartDelayMinMs, 5000);
            CharacterDelayMs = Clamp(CharacterDelayMs, 0, 500);
            JitterPercent = Clamp(JitterPercent, 0, 80);
            SubmitDelayMinMs = Clamp(SubmitDelayMinMs, 0, 2000);
            SubmitDelayMaxMs = Clamp(SubmitDelayMaxMs, SubmitDelayMinMs, 2000);
            TypingSubmitMinimumMs = Clamp(TypingSubmitMinimumMs, 0, 10000);
            TypingSubmitBudgetMs = Clamp(TypingSubmitBudgetMs,
                Math.Max(250, TypingSubmitMinimumMs), 10000);
        }

        private static int NextRange(Random random, int minimum, int maximum)
        {
            return maximum <= minimum ? minimum : random.Next(minimum, maximum + 1);
        }

        private static bool ReadBool(IDictionary<string, string> entries, string key, bool fallback)
        {
            string raw;
            bool value;
            return entries.TryGetValue(key, out raw) && Boolean.TryParse(raw, out value) ? value : fallback;
        }

        private static int ReadInt(IDictionary<string, string> entries, string key, int fallback, int minimum, int maximum)
        {
            string raw;
            int value;
            return entries.TryGetValue(key, out raw) && Int32.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) ? Clamp(value, minimum, maximum) : fallback;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }
    }
}
