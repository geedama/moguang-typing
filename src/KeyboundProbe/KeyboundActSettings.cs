using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ActEnhanced.KeyboundProbe
{
    public sealed class KeyboundTimingPlan
    {
        public string Mode;
        public int ReactionDelayMs;
        public int[] CharacterDelaysMs;
        public int SubmitDelayMs;
        public int TotalMinimumMs;
        public int TotalMaximumMs;

        public int PlannedTotalMs
        {
            get
            {
                int total = ReactionDelayMs + SubmitDelayMs;
                if (CharacterDelaysMs != null)
                    foreach (int delay in CharacterDelaysMs)
                        total += delay;
                return total;
            }
        }
    }

    public sealed class KeyboundTimingProfile
    {
        public string Mode;
        public int ReactionDelayMs;
        public int CharacterDelayMs;
        public int SubmitDelayMs;
        public int TotalMinimumMs;
        public int TotalMaximumMs;
        public int JitterPercent;
    }

    public sealed class KeyboundActSettings
    {
        public const string HumanMode = "human";
        public const string WarriorMode = "warrior";
        public const string CustomMode = "custom";

        public bool AutoLaunchDalamud = false;
        public string TimingMode = HumanMode;
        public int CustomReactionDelayMs = 150;
        public int CustomCharacterDelayMs = 70;
        public int CustomSubmitDelayMs = 150;
        public int CustomTotalMinimumMs = 1000;
        public int CustomTotalMaximumMs = 3000;
        public int CustomJitterPercent = 20;

        public static KeyboundActSettings Load(string path)
        {
            var value = new KeyboundActSettings();
            if (!File.Exists(path)) return value;
            try
            {
                var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    int separator = line.IndexOf('=');
                    if (separator <= 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    entries[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
                value.AutoLaunchDalamud = ReadBool(entries, "AutoLaunchDalamud", value.AutoLaunchDalamud);
                string mode;
                if (entries.TryGetValue("TimingMode", out mode) && IsMode(mode))
                {
                    value.TimingMode = mode.ToLowerInvariant();
                    value.CustomReactionDelayMs = ReadInt(entries, "CustomReactionDelayMs",
                        value.CustomReactionDelayMs, 0, 5000);
                    value.CustomCharacterDelayMs = ReadInt(entries, "CustomCharacterDelayMs",
                        value.CustomCharacterDelayMs, 0, 1000);
                    value.CustomSubmitDelayMs = ReadInt(entries, "CustomSubmitDelayMs",
                        value.CustomSubmitDelayMs, 0, 5000);
                    value.CustomTotalMinimumMs = ReadInt(entries, "CustomTotalMinimumMs",
                        value.CustomTotalMinimumMs, 0, 10000);
                    value.CustomTotalMaximumMs = ReadInt(entries, "CustomTotalMaximumMs",
                        value.CustomTotalMaximumMs, 100, 10000);
                    value.CustomJitterPercent = ReadInt(entries, "CustomJitterPercent",
                        value.CustomJitterPercent, 0, 80);
                }
                else MigrateLegacy(entries, value);
            }
            catch { }
            value.Normalize();
            return value;
        }

        public void Save(string path)
        {
            Normalize();
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(path, new[]
            {
                "# Moguang Typing settings",
                "AutoLaunchDalamud=" + AutoLaunchDalamud,
                "TimingMode=" + TimingMode,
                "CustomReactionDelayMs=" + CustomReactionDelayMs.ToString(CultureInfo.InvariantCulture),
                "CustomCharacterDelayMs=" + CustomCharacterDelayMs.ToString(CultureInfo.InvariantCulture),
                "CustomSubmitDelayMs=" + CustomSubmitDelayMs.ToString(CultureInfo.InvariantCulture),
                "CustomTotalMinimumMs=" + CustomTotalMinimumMs.ToString(CultureInfo.InvariantCulture),
                "CustomTotalMaximumMs=" + CustomTotalMaximumMs.ToString(CultureInfo.InvariantCulture),
                "CustomJitterPercent=" + CustomJitterPercent.ToString(CultureInfo.InvariantCulture)
            });
        }

        public KeyboundTimingProfile GetTimingProfile(string text)
        {
            Normalize();
            int length = String.IsNullOrEmpty(text) ? 0 : text.Length;
            if (TimingMode == WarriorMode)
                return Profile(WarriorMode, 50, 30, 50, 500, 500, 0);
            if (TimingMode == CustomMode)
                return Profile(CustomMode, CustomReactionDelayMs, CustomCharacterDelayMs,
                    CustomSubmitDelayMs, CustomTotalMinimumMs, CustomTotalMaximumMs,
                    CustomJitterPercent);
            return Profile(HumanMode, 150, 70, 150, length > 6 ? 2000 : 1000, 3000, 20);
        }

        public KeyboundTimingPlan CreateTimingPlan(string text, Random random)
        {
            KeyboundTimingProfile profile = GetTimingProfile(text);
            int characters = String.IsNullOrEmpty(text) ? 0 : text.Length;
            int totalMinimum = WithJitter(profile.TotalMinimumMs, profile.JitterPercent, random);
            int totalMaximum = WithJitter(profile.TotalMaximumMs, profile.JitterPercent, random);
            if (totalMaximum < totalMinimum) totalMaximum = totalMinimum;
            var plan = new KeyboundTimingPlan
            {
                Mode = profile.Mode,
                ReactionDelayMs = WithJitter(profile.ReactionDelayMs, profile.JitterPercent, random),
                CharacterDelaysMs = new int[characters],
                SubmitDelayMs = WithJitter(profile.SubmitDelayMs, profile.JitterPercent, random),
                TotalMinimumMs = totalMinimum,
                TotalMaximumMs = totalMaximum
            };
            for (int index = 0; index < characters; index++)
                plan.CharacterDelaysMs[index] = WithJitter(profile.CharacterDelayMs,
                    profile.JitterPercent, random);

            int total = plan.PlannedTotalMs;
            if (total < plan.TotalMinimumMs)
                plan.SubmitDelayMs += plan.TotalMinimumMs - total;
            else if (total > plan.TotalMaximumMs)
                ScaleToMaximum(plan, total);
            return plan;
        }

        public int NextEquivalentTypingDelay(string text, Random random)
        {
            return CreateTimingPlan(text, random).PlannedTotalMs;
        }

        internal void Normalize()
        {
            if (!IsMode(TimingMode)) TimingMode = HumanMode;
            TimingMode = TimingMode.ToLowerInvariant();
            CustomReactionDelayMs = Clamp(CustomReactionDelayMs, 0, 5000);
            CustomCharacterDelayMs = Clamp(CustomCharacterDelayMs, 0, 1000);
            CustomSubmitDelayMs = Clamp(CustomSubmitDelayMs, 0, 5000);
            CustomTotalMinimumMs = Clamp(CustomTotalMinimumMs, 0, 10000);
            CustomTotalMaximumMs = Clamp(CustomTotalMaximumMs,
                Math.Max(100, CustomTotalMinimumMs), 10000);
            CustomJitterPercent = Clamp(CustomJitterPercent, 0, 80);
        }

        private static void MigrateLegacy(IDictionary<string, string> entries, KeyboundActSettings value)
        {
            bool humanize = ReadBool(entries, "Humanize", true);
            bool blatant = ReadBool(entries, "BlatantMode", false);
            value.TimingMode = blatant ? WarriorMode : humanize ? HumanMode : CustomMode;
            int reactionMin = ReadInt(entries, "StartDelayMinMs", value.CustomReactionDelayMs, 0, 5000);
            int reactionMax = ReadInt(entries, "StartDelayMaxMs", reactionMin, 0, 5000);
            int submitMin = ReadInt(entries, "SubmitDelayMinMs", value.CustomSubmitDelayMs, 0, 5000);
            int submitMax = ReadInt(entries, "SubmitDelayMaxMs", submitMin, 0, 5000);
            value.CustomReactionDelayMs = (reactionMin + Math.Max(reactionMin, reactionMax)) / 2;
            value.CustomCharacterDelayMs = ReadInt(entries, "CharacterDelayMs",
                value.CustomCharacterDelayMs, 0, 1000);
            value.CustomSubmitDelayMs = (submitMin + Math.Max(submitMin, submitMax)) / 2;
            value.CustomTotalMinimumMs = ReadInt(entries, "TypingSubmitMinimumMs",
                value.CustomTotalMinimumMs, 0, 10000);
            value.CustomTotalMaximumMs = ReadInt(entries, "TypingSubmitBudgetMs",
                value.CustomTotalMaximumMs, 100, 10000);
            value.CustomJitterPercent = ReadInt(entries, "JitterPercent",
                value.CustomJitterPercent, 0, 80);
        }

        private static KeyboundTimingProfile Profile(string mode, int reaction, int character,
            int submit, int minimum, int maximum, int jitter)
        {
            return new KeyboundTimingProfile
            {
                Mode = mode,
                ReactionDelayMs = reaction,
                CharacterDelayMs = character,
                SubmitDelayMs = submit,
                TotalMinimumMs = minimum,
                TotalMaximumMs = maximum,
                JitterPercent = jitter
            };
        }

        private static int WithJitter(int value, int percent, Random random)
        {
            if (value <= 0 || percent <= 0 || random == null) return Math.Max(0, value);
            int delta = value * percent / 100;
            return NextRange(random, Math.Max(0, value - delta), value + delta);
        }

        private static void ScaleToMaximum(KeyboundTimingPlan plan, int total)
        {
            if (total <= 0) return;
            double scale = plan.TotalMaximumMs / (double)total;
            plan.ReactionDelayMs = (int)Math.Floor(plan.ReactionDelayMs * scale);
            plan.SubmitDelayMs = (int)Math.Floor(plan.SubmitDelayMs * scale);
            for (int index = 0; index < plan.CharacterDelaysMs.Length; index++)
                plan.CharacterDelaysMs[index] = (int)Math.Floor(
                    plan.CharacterDelaysMs[index] * scale);
            plan.SubmitDelayMs += Math.Max(0, plan.TotalMaximumMs - plan.PlannedTotalMs);
        }

        private static bool IsMode(string mode)
        {
            return String.Equals(mode, HumanMode, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(mode, WarriorMode, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(mode, CustomMode, StringComparison.OrdinalIgnoreCase);
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

        private static int ReadInt(IDictionary<string, string> entries, string key, int fallback,
            int minimum, int maximum)
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
