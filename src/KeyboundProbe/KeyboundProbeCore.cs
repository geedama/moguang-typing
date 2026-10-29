using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ActEnhanced.KeyboundProbe
{
    internal sealed class KeyboundCombatPlanner
    {
        public const int PhaseOneKillGoal = 25;
        public const uint ShieldType = 0x4E6F;
        public const uint RewardType = 0x4C88;
        public const uint EyeType = 0x4C87;
        public const uint CactuarType = 0x4C85;
        public const uint ScorpionType = 0x4C89;
        public const uint MorbolType = 0x4C8A;
        public const uint BehemothType = 0x4C8B;
        public const uint StormDestroyerType = 0x4C8C;
        public const uint MissileType = 0x4D71;
        public const uint StormMissileType = 0x4D72;
        public const uint FlowerBudType = 0x4D73;
        public const uint GrownFlowerType = 0x4D74;
        public const uint BehemothMeteorType = 0x4D75;
        public const uint BehemothBarrierType = 0x4D70;
        public const uint MineType = 0x4DD3;

        public const uint CurseGazeAction = 0xC179;
        public const uint CactuarNeedleAction = 0xC174;
        public const uint SlimeBurstAction = 0xC18C;
        public const uint DestructionCannonAction = 0xC223;
        public const uint ThunderBombAction = 0xC227;
        public const uint MotionDetectorScorpionAction = 0xC17C;
        public const uint MotionDetectorStormAction = 0xC226;
        public const uint ExplosionAction = 0xC22A;
        public const uint BehemothMeteorExplosionAction = 0xC190;

        // Stop no later than 1.5 seconds after the cast begins. ACT can deliver the
        // line late, so timestamps that have already crossed this point pause at once.
        private const int StopAfterCastStartMs = 1500;
        private const int ResumeAfterCastEndMs = 1000;

        private readonly Dictionary<uint, long> appearanceOrder =
            new Dictionary<uint, long>();
        private readonly Dictionary<uint, uint> actorTypes = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, KeyboundActiveCast> activeCasts =
            new Dictionary<uint, KeyboundActiveCast>();
        private readonly HashSet<uint> deadEntities = new HashSet<uint>();
        private readonly object sync = new object();
        private long nextAppearanceOrder;
        private uint directorPhase;
        private int combatStage;
        private int phaseOneKillCount;
        private DateTime pauseStartUtc = DateTime.MaxValue;
        private DateTime pauseUntilUtc = DateTime.MinValue;
        private uint pauseSourceId;
        private uint pauseActionId;
        private string pauseReason = String.Empty;

        public uint DirectorPhase { get { lock (sync) return directorPhase; } }
        public int CombatStage { get { lock (sync) return combatStage; } }
        public int PhaseOneKillCount { get { lock (sync) return phaseOneKillCount; } }
        public int TrackedTargetCount { get { lock (sync) return appearanceOrder.Count; } }
        public bool IsPhaseOneComplete { get { lock (sync) return phaseOneKillCount >= PhaseOneKillGoal; } }
        public bool CanAttack { get { lock (sync) return directorPhase == 2 && combatStage != 0; } }

        internal static bool CanUsePrompt(bool obstructed, bool visualInterferenceActive)
        {
            return !obstructed || visualInterferenceActive;
        }

        internal static DateTime ResolveCastStartTime(uint actionId, DateTime eventUtc,
            DateTime observedUtc)
        {
            // FFXIV_ACT_Plugin log timestamps can trail the callback clock by several
            // seconds. Using that stale timestamp makes stop casts pause immediately
            // and resume before the mechanic has visibly completed.
            return IsStopCastAction(actionId) ? observedUtc : eventUtc;
        }

        public void SetDirectorPhase(uint value)
        {
            lock (sync)
            {
                if (directorPhase == value)
                    return;
                directorPhase = value;
                if (value == 1)
                {
                    nextAppearanceOrder = 0;
                    appearanceOrder.Clear();
                    deadEntities.Clear();
                    activeCasts.Clear();
                    ClearPauseUnsafe();
                    if (combatStage == 0)
                        combatStage = 1;
                    if (combatStage == 1)
                        phaseOneKillCount = 0;
                }
                else if (value == 2 && combatStage == 0)
                {
                    combatStage = 1;
                }
                else if (value == 4 || value == 5)
                {
                    ResetChallengeUnsafe();
                    directorPhase = value;
                }
            }
        }

        public void ObserveActor(uint entityId, uint bnpcId)
        {
            if (entityId == 0 || !IsRelevantActorType(bnpcId))
                return;
            lock (sync)
            {
                actorTypes[entityId] = bnpcId;
                if (bnpcId == StormDestroyerType)
                    combatStage = 3;
                else if (combatStage < 2 && IsPhaseTwoType(bnpcId))
                    combatStage = 2;
            }
        }

        public void ObserveTarget(uint entityId)
        {
            if (entityId == 0)
                return;
            lock (sync)
            {
                if (deadEntities.Contains(entityId) || appearanceOrder.ContainsKey(entityId))
                    return;
                appearanceOrder.Add(entityId, ++nextAppearanceOrder);
            }
        }

        public long GetAppearanceOrder(uint entityId)
        {
            lock (sync)
            {
                long value;
                return appearanceOrder.TryGetValue(entityId, out value) ? value : Int64.MaxValue;
            }
        }

        public uint GetActorType(uint entityId)
        {
            lock (sync)
            {
                uint value;
                return actorTypes.TryGetValue(entityId, out value) ? value : 0;
            }
        }

        public int CompareTargets(uint leftEntityId, int leftSlot,
            uint rightEntityId, int rightSlot, uint focusedEntityId, DateTime nowUtc)
        {
            lock (sync)
            {
                if (combatStage == 0)
                    return 0;
                int priority = GetTargetPriorityUnsafe(leftEntityId, nowUtc)
                    .CompareTo(GetTargetPriorityUnsafe(rightEntityId, nowUtc));
                if (priority != 0)
                    return priority;
                if (leftEntityId == focusedEntityId && rightEntityId != focusedEntityId)
                    return -1;
                if (rightEntityId == focusedEntityId && leftEntityId != focusedEntityId)
                    return 1;
                long leftOrder;
                long rightOrder;
                if (!appearanceOrder.TryGetValue(leftEntityId, out leftOrder))
                    leftOrder = Int64.MaxValue;
                if (!appearanceOrder.TryGetValue(rightEntityId, out rightOrder))
                    rightOrder = Int64.MaxValue;
                int order = leftOrder.CompareTo(rightOrder);
                if (order != 0)
                    return order;
                order = leftSlot.CompareTo(rightSlot);
                return order != 0 ? order : leftEntityId.CompareTo(rightEntityId);
            }
        }

        public bool MarkDied(uint entityId)
        {
            if (entityId == 0)
                return false;
            lock (sync)
            {
                if (!deadEntities.Add(entityId))
                    return false;
                appearanceOrder.Remove(entityId);
                activeCasts.Remove(entityId);
                if (pauseSourceId == entityId)
                    ClearPauseUnsafe();
                if (combatStage == 1)
                {
                    phaseOneKillCount++;
                    if (phaseOneKillCount >= PhaseOneKillGoal)
                        combatStage = 2;
                }
                return true;
            }
        }

        public void RecordCastStart(uint sourceId, uint actionId, double castSeconds, DateTime utcTime)
        {
            if (sourceId == 0 || actionId == 0 || castSeconds <= 0)
                return;
            lock (sync)
            {
                var cast = new KeyboundActiveCast
                {
                    SourceId = sourceId,
                    ActionId = actionId,
                    StartUtc = utcTime,
                    EndUtc = utcTime.AddSeconds(castSeconds)
                };
                KeyboundActiveCast existing;
                if (!activeCasts.TryGetValue(sourceId, out existing) ||
                    existing.ActionId != actionId || cast.EndUtc > existing.EndUtc)
                    activeCasts[sourceId] = cast;
                if (IsStopCastAction(actionId))
                {
                    DateTime start = cast.StartUtc.AddMilliseconds(StopAfterCastStartMs);
                    DateTime until = cast.EndUtc.AddMilliseconds(ResumeAfterCastEndMs);
                    if (until > pauseUntilUtc)
                    {
                        pauseStartUtc = start;
                        pauseUntilUtc = until;
                        pauseSourceId = sourceId;
                        pauseActionId = actionId;
                        pauseReason = actionId == CurseGazeAction ? "诅咒视线" : "运动体探知干扰器";
                    }
                }
            }
        }

        public void RecordCastEnd(uint sourceId, uint actionId, bool interrupted)
        {
            lock (sync)
            {
                KeyboundActiveCast cast;
                if (activeCasts.TryGetValue(sourceId, out cast) &&
                    (actionId == 0 || cast.ActionId == actionId))
                    activeCasts.Remove(sourceId);
                if (interrupted && pauseSourceId == sourceId &&
                    (actionId == 0 || pauseActionId == actionId))
                    ClearPauseUnsafe();
            }
        }

        public bool ShouldPause(DateTime nowUtc)
        {
            lock (sync)
                return nowUtc >= pauseStartUtc && nowUtc <= pauseUntilUtc;
        }

        public string PauseDescription(DateTime nowUtc)
        {
            lock (sync)
            {
                if (nowUtc < pauseStartUtc || nowUtc > pauseUntilUtc)
                    return String.Empty;
                int remaining = Math.Max(0, (int)Math.Ceiling((pauseUntilUtc - nowUtc).TotalMilliseconds));
                return pauseReason + "：停止输入，约 " + remaining + "ms 后恢复";
            }
        }

        public string Describe()
        {
            lock (sync)
            {
                if (combatStage == 1)
                    return "P1 先出现先攻击；击杀 " + phaseOneKillCount + "/" + PhaseOneKillGoal;
                if (combatStage == 2)
                    return "P2 机制优先：护盾 > 四次打断 > 奖励怪 > 导弹/陨石/自爆物 > 当前目标";
                if (combatStage == 3)
                    return "P3 机制优先：护盾 > 四次攻击读条 > 导弹/地雷 > 暴风破坏者";
                return "等待识别 P1/P2/P3";
            }
        }

        private int GetTargetPriorityUnsafe(uint entityId, DateTime nowUtc)
        {
            uint bnpcId;
            actorTypes.TryGetValue(entityId, out bnpcId);
            KeyboundActiveCast cast;
            activeCasts.TryGetValue(entityId, out cast);
            uint actionId = cast != null && cast.EndUtc.AddSeconds(1) >= nowUtc ? cast.ActionId : 0;

            if (bnpcId == ShieldType || bnpcId == BehemothBarrierType)
                return 0;
            if (actionId == CactuarNeedleAction || actionId == SlimeBurstAction ||
                actionId == DestructionCannonAction || actionId == ThunderBombAction)
                return 5;
            if (combatStage == 3)
            {
                if (bnpcId == MineType || bnpcId == StormMissileType)
                    return 10;
                if (bnpcId == StormDestroyerType)
                    return 100;
                return 60;
            }
            if (combatStage == 2)
            {
                if (bnpcId == RewardType)
                    return 10;
                if (bnpcId == MissileType || bnpcId == MineType ||
                    bnpcId == BehemothMeteorType || actionId == ExplosionAction ||
                    actionId == BehemothMeteorExplosionAction)
                    return 20;
                if (bnpcId == FlowerBudType || bnpcId == GrownFlowerType)
                    return 30;
                if (bnpcId == ScorpionType || bnpcId == MorbolType || bnpcId == BehemothType)
                    return 100;
                return 60;
            }
            return 50;
        }

        private static bool IsPhaseTwoType(uint bnpcId)
        {
            return bnpcId == RewardType || bnpcId == EyeType || bnpcId == CactuarType ||
                bnpcId == ScorpionType || bnpcId == MorbolType || bnpcId == BehemothType ||
                bnpcId == MissileType || bnpcId == FlowerBudType ||
                bnpcId == GrownFlowerType || bnpcId == BehemothMeteorType ||
                bnpcId == BehemothBarrierType;
        }

        public static bool IsRelevantActorType(uint bnpcId)
        {
            return (bnpcId >= 0x4C80 && bnpcId <= 0x4C8F) ||
                (bnpcId >= 0x4D70 && bnpcId <= 0x4D7F) ||
                bnpcId == MineType || bnpcId == ShieldType || bnpcId == 0x233C;
        }

        internal static bool IsStopCastAction(uint actionId)
        {
            return actionId == CurseGazeAction || actionId == MotionDetectorScorpionAction ||
                actionId == MotionDetectorStormAction;
        }

        private void ResetChallengeUnsafe()
        {
            combatStage = 0;
            phaseOneKillCount = 0;
            nextAppearanceOrder = 0;
            appearanceOrder.Clear();
            actorTypes.Clear();
            activeCasts.Clear();
            deadEntities.Clear();
            ClearPauseUnsafe();
        }

        private void ClearPauseUnsafe()
        {
            pauseStartUtc = DateTime.MaxValue;
            pauseUntilUtc = DateTime.MinValue;
            pauseSourceId = 0;
            pauseActionId = 0;
            pauseReason = String.Empty;
        }
    }

    internal sealed class KeyboundActiveCast
    {
        public uint SourceId;
        public uint ActionId;
        public DateTime StartUtc;
        public DateTime EndUtc;
    }

    internal sealed class DirectPromptCompletionTracker
    {
        private readonly Dictionary<uint, string> completedByEntity =
            new Dictionary<uint, string>();

        public bool IsSuppressed(uint entityId, string text)
        {
            string completed;
            return completedByEntity.TryGetValue(entityId, out completed) &&
                String.Equals(completed, text ?? String.Empty, StringComparison.Ordinal);
        }

        public void MarkCompleted(uint entityId, string text)
        {
            completedByEntity[entityId] = text ?? String.Empty;
        }

        public bool Observe(uint entityId, string text)
        {
            string completed;
            if (!completedByEntity.TryGetValue(entityId, out completed) ||
                String.Equals(completed, text ?? String.Empty, StringComparison.Ordinal))
                return false;
            completedByEntity.Remove(entityId);
            return true;
        }

        public void RemoveEntity(uint entityId)
        {
            completedByEntity.Remove(entityId);
        }

        public void Clear()
        {
            completedByEntity.Clear();
        }
    }

    public sealed class PacketHeader
    {
        public uint MessageLength;
        public uint ActorId;
        public ushort Opcode;
    }

    public static class PacketMetadata
    {
        public const int HeaderSize = 32;
        public const int OpcodeOffset = 18;
        public const int ActorCastActionOffset = 32;
        public const int ActorCastTimeOffset = 40;

        public static bool TryReadHeader(byte[] message, out PacketHeader header)
        {
            header = null;
            if (message == null || message.Length < HeaderSize)
                return false;

            header = new PacketHeader
            {
                MessageLength = BitConverter.ToUInt32(message, 0),
                ActorId = BitConverter.ToUInt32(message, 4),
                Opcode = BitConverter.ToUInt16(message, OpcodeOffset)
            };
            return true;
        }

        public static ushort LoadOpcode(string path, string name)
        {
            if (String.IsNullOrWhiteSpace(path) || String.IsNullOrWhiteSpace(name) ||
                !File.Exists(path))
                return 0;
            try
            {
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    int separator = line.IndexOf('|');
                    if (separator <= 0 || !String.Equals(line.Substring(0, separator).Trim(),
                        name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ushort value;
                    return UInt16.TryParse(line.Substring(separator + 1).Trim(),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ? value : (ushort)0;
                }
            }
            catch { }
            return 0;
        }

        public static bool TryReadActorCast(byte[] message, ushort actorCastOpcode,
            out uint actorId, out uint actionId, out double castSeconds)
        {
            actorId = 0;
            actionId = 0;
            castSeconds = 0;
            PacketHeader header;
            if (actorCastOpcode == 0 || message == null ||
                message.Length < ActorCastTimeOffset + sizeof(float) ||
                !TryReadHeader(message, out header) || header.Opcode != actorCastOpcode)
                return false;
            float seconds = BitConverter.ToSingle(message, ActorCastTimeOffset);
            if (Single.IsNaN(seconds) || Single.IsInfinity(seconds) || seconds <= 0 || seconds > 120)
                return false;
            actorId = header.ActorId;
            actionId = BitConverter.ToUInt16(message, ActorCastActionOffset);
            castSeconds = seconds;
            return actorId != 0 && actionId != 0;
        }

        public static uint Fingerprint(byte[] message)
        {
            unchecked
            {
                uint hash = 2166136261;
                if (message == null)
                    return hash;
                for (int index = 0; index < message.Length; index++)
                {
                    hash ^= message[index];
                    hash *= 16777619;
                }
                return hash;
            }
        }

        public static string ExtractPrintable(byte[] message)
        {
            if (message == null || message.Length <= HeaderSize)
                return String.Empty;

            var result = new StringBuilder();
            var run = new StringBuilder();
            for (int index = HeaderSize; index < message.Length; index++)
            {
                byte value = message[index];
                if (value >= 0x20 && value <= 0x7e)
                {
                    run.Append((char)value);
                }
                else
                {
                    AppendRun(result, run);
                }
            }
            AppendRun(result, run);
            string text = result.ToString();
            return text.Length <= 240 ? text : text.Substring(0, 240);
        }

        private static void AppendRun(StringBuilder result, StringBuilder run)
        {
            if (run.Length >= 4)
            {
                if (result.Length > 0)
                    result.Append(" | ");
                result.Append(run.ToString().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
            }
            run.Length = 0;
        }
    }

    internal sealed class CaptureRecord
    {
        public byte Kind;
        public DateTime UtcTime;
        public long Epoch;
        public string Connection;
        public byte[] Message;
        public string Label;
        public string Nearby;
    }

    internal sealed class RecentPacket
    {
        public DateTime UtcTime;
        public ushort Opcode;
        public int Length;
    }

    internal sealed class OpcodeStats
    {
        public long Count;
        public int MinLength = Int32.MaxValue;
        public int MaxLength;
        public DateTime FirstUtc;
        public DateTime LastUtc;
        public readonly HashSet<uint> Fingerprints = new HashSet<uint>();
    }

    public sealed class ProbeCaptureSession : IDisposable
    {
        private const byte PacketRecord = 1;
        private const byte MarkerRecord = 2;
        private const byte EndRecord = 3;
        private const int QueueLimit = 25000;
        private const long MaxBinaryPartBytes = 256L * 1024L * 1024L;
        private static readonly TimeSpan NearbyWindow = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RecentRetention = TimeSpan.FromSeconds(12);

        private readonly BlockingCollection<CaptureRecord> queue =
            new BlockingCollection<CaptureRecord>(new ConcurrentQueue<CaptureRecord>(), QueueLimit);
        private readonly List<RecentPacket> recent = new List<RecentPacket>();
        private readonly object recentLock = new object();
        private readonly Thread writerThread;
        private readonly DateTime startedUtc;
        private readonly string binaryPath;
        private readonly string binaryStem;
        private readonly string eventPath;
        private readonly string summaryPath;
        private readonly List<string> binaryParts = new List<string>();
        private readonly object partLock = new object();
        private volatile bool accepting = true;
        private long acceptedPackets;
        private long droppedPackets;
        private Exception writerError;

        public ProbeCaptureSession(string captureDirectory)
        {
            if (String.IsNullOrWhiteSpace(captureDirectory))
                throw new ArgumentException("Capture directory is required.", "captureDirectory");

            Directory.CreateDirectory(captureDirectory);
            startedUtc = DateTime.UtcNow;
            string stem = "keybound-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            binaryStem = Path.Combine(captureDirectory, stem);
            binaryPath = binaryStem + ".part001.kbcap";
            eventPath = Path.Combine(captureDirectory, stem + ".events.tsv");
            summaryPath = Path.Combine(captureDirectory, stem + ".summary.txt");

            writerThread = new Thread(WriterMain);
            writerThread.IsBackground = true;
            writerThread.Name = "KeyboundProbe capture writer";
            writerThread.Start();
        }

        public string BinaryPath { get { return binaryPath; } }
        public string EventPath { get { return eventPath; } }
        public string SummaryPath { get { return summaryPath; } }
        public long AcceptedPackets { get { return Interlocked.Read(ref acceptedPackets); } }
        public long DroppedPackets { get { return Interlocked.Read(ref droppedPackets); } }
        public string WriterError { get { return writerError == null ? null : writerError.Message; } }
        public bool IsAccepting { get { return accepting; } }
        public DateTime StartedUtc { get { return startedUtc; } }
        public long CurrentBinaryBytes
        {
            get
            {
                long total = 0;
                string[] parts;
                lock (partLock)
                    parts = binaryParts.ToArray();
                foreach (string part in parts)
                {
                    try { total += new FileInfo(part).Length; }
                    catch { }
                }
                return total;
            }
        }

        public bool Record(string connection, long epoch, byte[] message)
        {
            if (!accepting || message == null)
                return false;

            PacketHeader header;
            if (!PacketMetadata.TryReadHeader(message, out header))
                return false;

            DateTime now = DateTime.UtcNow;
            lock (recentLock)
            {
                recent.Add(new RecentPacket { UtcTime = now, Opcode = header.Opcode, Length = message.Length });
                DateTime cutoff = now - RecentRetention;
                int removeCount = 0;
                while (removeCount < recent.Count && recent[removeCount].UtcTime < cutoff)
                    removeCount++;
                if (removeCount > 0)
                    recent.RemoveRange(0, removeCount);
            }

            var copy = new byte[message.Length];
            Buffer.BlockCopy(message, 0, copy, 0, message.Length);
            bool added = queue.TryAdd(new CaptureRecord
            {
                Kind = PacketRecord,
                UtcTime = now,
                Epoch = epoch,
                Connection = connection ?? String.Empty,
                Message = copy
            });
            if (added)
                Interlocked.Increment(ref acceptedPackets);
            else
                Interlocked.Increment(ref droppedPackets);
            return added;
        }

        public void Mark(string label)
        {
            if (!accepting)
                return;

            DateTime now = DateTime.UtcNow;
            string nearby;
            lock (recentLock)
            {
                DateTime cutoff = now - NearbyWindow;
                nearby = String.Join(", ", recent
                    .Where(value => value.UtcTime >= cutoff)
                    .GroupBy(value => value.Opcode)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .Select(group => "0x" + group.Key.ToString("X4") + "=" + group.Count())
                    .Take(30)
                    .ToArray());
            }

            queue.TryAdd(new CaptureRecord
            {
                Kind = MarkerRecord,
                UtcTime = now,
                Label = String.IsNullOrWhiteSpace(label) ? "MARK" : label.Trim(),
                Nearby = nearby
            });
        }

        public void Stop()
        {
            if (!accepting)
                return;
            accepting = false;
            queue.TryAdd(new CaptureRecord { Kind = EndRecord, UtcTime = DateTime.UtcNow });
            queue.CompleteAdding();
            if (!writerThread.Join(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Capture writer did not stop within 10 seconds.");
            if (writerError != null)
                throw new IOException("Capture writer failed.", writerError);
        }

        public void Dispose()
        {
            Stop();
            queue.Dispose();
        }

        private void WriterMain()
        {
            var stats = new Dictionary<ushort, OpcodeStats>();
            var markers = new List<CaptureRecord>();
            try
            {
                using (var events = new StreamWriter(eventPath, false, new UTF8Encoding(false)))
                {
                    BinaryWriter binary = null;
                    int partNumber = 1;
                    try
                    {
                        binary = OpenBinaryPart(partNumber);
                        events.WriteLine("utc\tdelta_ms\tkind\topcode\tlength\tactor_id\tfingerprint\tconnection\tprintable_or_marker\tnearby_3s");

                        foreach (CaptureRecord record in queue.GetConsumingEnumerable())
                        {
                            long estimatedBytes = record.Kind == PacketRecord
                                ? record.Message.Length + 64L
                                : 512L;
                            if (binary.BaseStream.Position + estimatedBytes > MaxBinaryPartBytes)
                            {
                                binary.Flush();
                                binary.Dispose();
                                binary = OpenBinaryPart(++partNumber);
                            }

                            WriteBinary(binary, record);
                            if (record.Kind == PacketRecord)
                                WritePacketEvent(events, record, stats);
                            else if (record.Kind == MarkerRecord)
                            {
                                markers.Add(record);
                                WriteMarkerEvent(events, record);
                            }
                            else if (record.Kind == EndRecord)
                                events.WriteLine(FormatTime(record.UtcTime) + "\t" + Delta(record.UtcTime) + "\tEND");
                        }
                        events.Flush();
                        binary.Flush();
                    }
                    finally
                    {
                        if (binary != null)
                            binary.Dispose();
                    }
                }
                WriteSummary(stats, markers);
            }
            catch (Exception ex)
            {
                writerError = ex;
            }
        }

        private BinaryWriter OpenBinaryPart(int partNumber)
        {
            string path = binaryStem + ".part" + partNumber.ToString("000", CultureInfo.InvariantCulture) + ".kbcap";
            lock (partLock)
                binaryParts.Add(path);
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(Encoding.ASCII.GetBytes("KBP1"));
            writer.Write(1);
            writer.Write(startedUtc.Ticks);
            writer.Write(partNumber);
            return writer;
        }

        private void WriteBinary(BinaryWriter writer, CaptureRecord record)
        {
            writer.Write(record.Kind);
            writer.Write(record.UtcTime.Ticks);
            if (record.Kind == PacketRecord)
            {
                writer.Write(record.Epoch);
                writer.Write(record.Connection ?? String.Empty);
                writer.Write(record.Message.Length);
                writer.Write(record.Message);
            }
            else if (record.Kind == MarkerRecord)
            {
                writer.Write(record.Label ?? String.Empty);
                writer.Write(record.Nearby ?? String.Empty);
            }
        }

        private void WritePacketEvent(StreamWriter writer, CaptureRecord record,
            Dictionary<ushort, OpcodeStats> stats)
        {
            PacketHeader header;
            if (!PacketMetadata.TryReadHeader(record.Message, out header))
                return;

            uint fingerprint = PacketMetadata.Fingerprint(record.Message);
            OpcodeStats value;
            if (!stats.TryGetValue(header.Opcode, out value))
            {
                value = new OpcodeStats { FirstUtc = record.UtcTime };
                stats.Add(header.Opcode, value);
            }
            value.Count++;
            value.LastUtc = record.UtcTime;
            value.MinLength = Math.Min(value.MinLength, record.Message.Length);
            value.MaxLength = Math.Max(value.MaxLength, record.Message.Length);
            if (value.Fingerprints.Count < 4096)
                value.Fingerprints.Add(fingerprint);

            writer.WriteLine(String.Join("\t", new[]
            {
                FormatTime(record.UtcTime),
                Delta(record.UtcTime),
                "PACKET",
                "0x" + header.Opcode.ToString("X4"),
                record.Message.Length.ToString(CultureInfo.InvariantCulture),
                "0x" + header.ActorId.ToString("X8"),
                "0x" + fingerprint.ToString("X8"),
                Clean(record.Connection),
                Clean(PacketMetadata.ExtractPrintable(record.Message)),
                String.Empty
            }));
        }

        private void WriteMarkerEvent(StreamWriter writer, CaptureRecord record)
        {
            writer.WriteLine(String.Join("\t", new[]
            {
                FormatTime(record.UtcTime),
                Delta(record.UtcTime),
                "MARKER",
                String.Empty,
                String.Empty,
                String.Empty,
                String.Empty,
                String.Empty,
                Clean(record.Label),
                Clean(record.Nearby)
            }));
            writer.Flush();
        }

        private void WriteSummary(Dictionary<ushort, OpcodeStats> stats, List<CaptureRecord> markers)
        {
            using (var writer = new StreamWriter(summaryPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("Keybound Probe capture summary");
                writer.WriteLine("Started local: " + startedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
                writer.WriteLine("Packets: " + AcceptedPackets);
                writer.WriteLine("Dropped: " + DroppedPackets);
                writer.WriteLine("Raw capture parts: " + binaryParts.Count + " (256 MiB maximum per part)");
                writer.WriteLine();
                writer.WriteLine("Markers (nearby opcodes are the 3 seconds before the click):");
                foreach (CaptureRecord marker in markers)
                    writer.WriteLine("  +" + Delta(marker.UtcTime) + " " + marker.Label + " :: " + marker.Nearby);
                writer.WriteLine();
                writer.WriteLine("Opcode totals:");
                writer.WriteLine("opcode\tcount\tunique_fingerprints\tlength_range\tfirst_ms\tlast_ms");
                foreach (KeyValuePair<ushort, OpcodeStats> pair in stats.OrderByDescending(value => value.Value.Count))
                {
                    OpcodeStats value = pair.Value;
                    writer.WriteLine("0x" + pair.Key.ToString("X4") + "\t" + value.Count + "\t" +
                        value.Fingerprints.Count + "\t" + value.MinLength + "-" + value.MaxLength + "\t" +
                        Delta(value.FirstUtc) + "\t" + Delta(value.LastUtc));
                }
            }
        }

        private string Delta(DateTime utc)
        {
            return Math.Max(0, (long)(utc - startedUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatTime(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        private static string Clean(string value)
        {
            return (value ?? String.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
