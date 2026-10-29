using System;
using System.Collections.Generic;
using System.Globalization;

namespace ActEnhanced.KeyboundProbe
{
    internal enum KeyboundCombatSignalKind
    {
        ActorAdded,
        CastStarted,
        CastEnded,
        StatusAdded,
        StatusRemoved
    }

    internal sealed class KeyboundCombatSignal
    {
        public KeyboundCombatSignalKind Kind;
        public DateTime UtcTime;
        public uint EntityId;
        public uint BNpcId;
        public long MaxHp;
        public uint ActionId;
        public string ActionName;
        public double CastSeconds;
        public bool Interrupted;
        public uint StatusId;
        public string StatusName;
        public uint SourceId;
        public uint TargetId;
        public double DurationSeconds;
    }

    internal sealed class KeyboundCombatLogParser
    {
        public bool TryParse(string line, out KeyboundCombatSignal result)
        {
            result = null;
            if (String.IsNullOrWhiteSpace(line))
                return false;
            string[] fields = line.Split('|');
            if (fields.Length < 4)
                return false;
            DateTime utcTime = ParseTime(fields[1]);
            string type = fields[0].Trim();

            if (type == "261" && fields.Length >= 6 && fields[2] == "Add")
            {
                uint entityId;
                if (!TryHex(fields[3], out entityId))
                    return false;
                Dictionary<string, string> values = ParsePairs(fields, 4);
                uint bnpcId;
                long maxHp;
                TryHex(Get(values, "BNpcID"), out bnpcId);
                Int64.TryParse(Get(values, "MaxHP"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out maxHp);
                if (bnpcId == 0)
                    return false;
                result = new KeyboundCombatSignal
                {
                    Kind = KeyboundCombatSignalKind.ActorAdded,
                    UtcTime = utcTime,
                    EntityId = entityId,
                    BNpcId = bnpcId,
                    MaxHp = maxHp
                };
                return true;
            }

            if (type == "20" && fields.Length >= 9)
            {
                uint sourceId;
                uint actionId;
                double castSeconds;
                if (!TryHex(fields[2], out sourceId) || !TryHex(fields[4], out actionId) ||
                    !Double.TryParse(fields[8], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out castSeconds))
                    return false;
                result = new KeyboundCombatSignal
                {
                    Kind = KeyboundCombatSignalKind.CastStarted,
                    UtcTime = utcTime,
                    EntityId = sourceId,
                    SourceId = sourceId,
                    ActionId = actionId,
                    ActionName = fields[5],
                    CastSeconds = castSeconds
                };
                return true;
            }

            if (type == "23" && fields.Length >= 7)
            {
                uint sourceId;
                uint actionId;
                if (!TryHex(fields[2], out sourceId) || !TryHex(fields[4], out actionId))
                    return false;
                result = new KeyboundCombatSignal
                {
                    Kind = KeyboundCombatSignalKind.CastEnded,
                    UtcTime = utcTime,
                    EntityId = sourceId,
                    SourceId = sourceId,
                    ActionId = actionId,
                    ActionName = fields[5],
                    Interrupted = String.Equals(fields[6], "Interrupted", StringComparison.OrdinalIgnoreCase)
                };
                return true;
            }

            if ((type == "26" || type == "30") && fields.Length >= 9)
            {
                uint statusId;
                uint sourceId;
                uint targetId;
                double duration;
                if (!TryHex(fields[2], out statusId))
                    return false;
                TryHex(fields[5], out sourceId);
                TryHex(fields[7], out targetId);
                Double.TryParse(fields[4], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out duration);
                result = new KeyboundCombatSignal
                {
                    Kind = type == "26" ? KeyboundCombatSignalKind.StatusAdded :
                        KeyboundCombatSignalKind.StatusRemoved,
                    UtcTime = utcTime,
                    EntityId = targetId,
                    SourceId = sourceId,
                    TargetId = targetId,
                    StatusId = statusId,
                    StatusName = fields[3],
                    DurationSeconds = duration
                };
                return true;
            }
            return false;
        }

        private static Dictionary<string, string> ParsePairs(string[] fields, int start)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = start; index + 1 < fields.Length - 1; index += 2)
                result[fields[index]] = fields[index + 1];
            return result;
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : String.Empty;
        }

        private static DateTime ParseTime(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out parsed) ? parsed.UtcDateTime : DateTime.UtcNow;
        }

        private static bool TryHex(string value, out uint parsed)
        {
            return UInt32.TryParse((value ?? String.Empty).Trim(), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out parsed);
        }
    }
}
