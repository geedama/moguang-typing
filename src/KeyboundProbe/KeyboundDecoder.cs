using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ActEnhanced.KeyboundProbe
{
    public sealed class KeyboundWord
    {
        public uint Id;
        public string DisplayText;
        public string InputText;
        public string InputKeys;
    }

    public sealed class KeyboundWordTable
    {
        private readonly Dictionary<uint, KeyboundWord> words = new Dictionary<uint, KeyboundWord>();
        private readonly Dictionary<string, KeyboundWord> wordsByDisplayText =
            new Dictionary<string, KeyboundWord>(StringComparer.Ordinal);
        private readonly List<KeyboundWord> allWords = new List<KeyboundWord>();

        public int Count { get { return words.Count; } }

        public static KeyboundWordTable Load(string path)
        {
            var table = new KeyboundWordTable();
            if (!File.Exists(path))
                return table;

            foreach (string line in File.ReadLines(path))
            {
                if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string[] fields = line.Split('\t');
                uint id;
                if (fields.Length < 3 || !UInt32.TryParse(fields[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out id))
                    continue;
                var word = new KeyboundWord
                {
                    Id = id,
                    DisplayText = fields[1],
                    InputText = fields[2],
                    InputKeys = fields.Length >= 4 && !String.IsNullOrWhiteSpace(fields[3])
                        ? fields[3].Trim().ToLowerInvariant()
                        : DeriveInitials(fields[2])
                };
                table.words[id] = word;
                table.allWords.Add(word);
                string displayKey = NormalizeDisplayText(word.DisplayText);
                if (displayKey.Length != 0 && !table.wordsByDisplayText.ContainsKey(displayKey))
                    table.wordsByDisplayText[displayKey] = word;
            }
            return table;
        }

        public KeyboundWord Get(uint id)
        {
            KeyboundWord word;
            return words.TryGetValue(id, out word) ? word : null;
        }

        public KeyboundWord FindByDisplayText(string displayText)
        {
            KeyboundWord word;
            string key = NormalizeDisplayText(displayText);
            return key.Length != 0 && wordsByDisplayText.TryGetValue(key, out word) ? word : null;
        }

        public KeyboundWord GetRandomDifferent(string excludedDisplayText, Random random)
        {
            if (random == null) throw new ArgumentNullException("random");
            if (allWords.Count == 0) return null;
            string excluded = NormalizeDisplayText(excludedDisplayText);
            int start = random.Next(0, allWords.Count);
            for (int offset = 0; offset < allWords.Count; offset++)
            {
                KeyboundWord candidate = allWords[(start + offset) % allWords.Count];
                if (!String.Equals(NormalizeDisplayText(candidate.DisplayText), excluded,
                    StringComparison.Ordinal))
                    return candidate;
            }
            return null;
        }

        private static string NormalizeDisplayText(string value)
        {
            var result = new System.Text.StringBuilder();
            foreach (char character in value ?? String.Empty)
                if (character >= '\u4e00' && character <= '\u9fff')
                    result.Append(character);
            return result.ToString();
        }

        private static string DeriveInitials(string pinyin)
        {
            var result = new System.Text.StringBuilder();
            foreach (char value in pinyin ?? String.Empty)
            {
                if (value >= 'A' && value <= 'Z')
                    result.Append(Char.ToLowerInvariant(value));
            }
            return result.ToString();
        }
    }

    public enum KeyboundEventKind
    {
        Prompt,
        InputSuccess,
        MonsterDied,
        Score,
        Phase,
        Other
    }

    public sealed class KeyboundEvent
    {
        public DateTime Time;
        public KeyboundEventKind Kind;
        public uint Command;
        public uint MonsterId;
        public uint WordId;
        public uint Score;
        public uint Phase;
        public uint Data1;
        public uint Data2;
        public KeyboundWord Word;
    }

    public sealed class KeyboundLogDecoder
    {
        public const uint DirectorInstance = 0x8003DEA9;
        public const uint PromptCommand = 0x10000008;
        public const uint InputSuccessCommand = 0x10000016;
        public const uint MonsterDiedCommand = 0x1000000B;
        public const uint ScoreCommand = 0x1000000A;
        public const uint PhaseCommand = 0x10000013;

        private readonly KeyboundWordTable words;

        public KeyboundLogDecoder(KeyboundWordTable words)
        {
            this.words = words ?? new KeyboundWordTable();
        }

        public bool TryDecode(uint eventType, string line, out KeyboundEvent result)
        {
            result = null;
            if (String.IsNullOrEmpty(line))
                return false;

            string[] fields = line.Split('|');
            int instanceIndex = -1;
            for (int index = 0; index < fields.Length; index++)
            {
                uint value;
                if (TryHex(fields[index], out value) && value == DirectorInstance)
                {
                    instanceIndex = index;
                    break;
                }
            }
            if (instanceIndex < 0 || fields.Length <= instanceIndex + 5)
                return false;

            uint command;
            uint data0;
            uint data1;
            uint data2;
            uint data3;
            if (!TryHex(fields[instanceIndex + 1], out command) ||
                !TryHex(fields[instanceIndex + 2], out data0) ||
                !TryHex(fields[instanceIndex + 3], out data1) ||
                !TryHex(fields[instanceIndex + 4], out data2) ||
                !TryHex(fields[instanceIndex + 5], out data3))
                return false;

            var decoded = new KeyboundEvent
            {
                Time = DateTime.Now,
                Command = command,
                Data1 = data1,
                Data2 = data2,
                Kind = KeyboundEventKind.Other
            };

            if (command == PromptCommand)
            {
                decoded.Kind = KeyboundEventKind.Prompt;
                decoded.MonsterId = data0;
                decoded.WordId = data3;
                decoded.Word = words.Get(data3);
            }
            else if (command == InputSuccessCommand)
            {
                decoded.Kind = KeyboundEventKind.InputSuccess;
                decoded.MonsterId = data0;
            }
            else if (command == MonsterDiedCommand)
            {
                decoded.Kind = KeyboundEventKind.MonsterDied;
                decoded.MonsterId = data0;
            }
            else if (command == ScoreCommand)
            {
                decoded.Kind = KeyboundEventKind.Score;
                decoded.Score = data0;
            }
            else if (command == PhaseCommand)
            {
                decoded.Kind = KeyboundEventKind.Phase;
                decoded.Phase = data0;
            }
            else
            {
                return false;
            }

            result = decoded;
            return true;
        }

        private static bool TryHex(string value, out uint parsed)
        {
            return UInt32.TryParse((value ?? String.Empty).Trim(), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out parsed);
        }
    }
}
