using System;
using System.IO;
using System.Net;
using System.Text;
using ActEnhanced.KeyboundProbe;

internal static class KeyboundProbeTests
{
    private static int failures;

    private static void Main(string[] args)
    {
        KeyboundAfkGuardTests.Run(Assert);
        if (args.Length == 1 && args[0] == "afk")
        {
            Environment.ExitCode = failures == 0 ? 0 : 1;
            Console.WriteLine(failures == 0 ? "PASS: AFK guard tests" : "FAIL: AFK guard tests");
            return;
        }
        if (args.Length == 1 && args[0] == "flow")
        {
            TestRepeatedFlow();
            TestSingleRun();
            TestSingleRunSystemMenu();
            TestActSettings();
            TestOverlayServer();
            Environment.ExitCode = failures == 0 ? 0 : 1;
            Console.WriteLine(failures == 0 ? "PASS: automation flow tests" : "FAIL: automation flow tests");
            return;
        }
        TestHeader();
        TestActorCastPacket();
        TestCapture();
        TestDecoder();
        TestKtgMemoryLayout();
        TestSingleRun();
        TestRepeatedFlow();
        TestSingleRunSystemMenu();
        TestDirectPromptCompletionTracker();
        TestCombatPlanner();
        TestCombatLogParser();
        TestBridgeProtocol();
        TestActSettings();
        TestDalamudLauncherGuards();
        TestGameInputGuards();
        TestPhysicalPinyinSchedule();
        TestOverlayServer();
        Assert(GameInputSender.DirectTypingToSubmitMinimumMs == 1500 &&
            GameInputSender.DirectTypingToSubmitBudgetMs == 4000,
            "direct typing-to-submit dynamic range");
        Assert(KeyboundOverlayServer.JsonString("题目\n\"测试\"") == "\"题目\\n\\\"测试\\\"\"",
            "overlay JSON escaping");
        if (failures != 0)
        {
            Console.Error.WriteLine("FAIL: " + failures + " test(s)");
            Environment.Exit(1);
        }
        Console.WriteLine("PASS: KeyboundProbe core tests");
    }

    private static void TestPhysicalPinyinSchedule()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            int interval = PhysicalPinyinSchedule.NextIntervalMilliseconds(new Random(seed));
            Assert(interval >= PhysicalPinyinSchedule.MinimumIntervalMilliseconds &&
                interval <= PhysicalPinyinSchedule.MaximumIntervalMilliseconds,
                "physical pinyin interval remains within 45-60 seconds");
        }
    }

    private static void TestSingleRunSystemMenu()
    {
        DateTime now = DateTime.UtcNow;
        var flow = new KeyboundSingleRun();
        var snapshot = new KtgMemorySnapshot { ProcessId = 1,
            AfkTimer = new KtgAfkTimerSnapshot { TerritoryTypeId = 1360 },
            LocalPlayer = new KtgObjectProbe() };
        snapshot.Objects.Add(new KtgObjectProbe { BaseId = 1058751, Name = "报名登记员",
            ObjectKind = 3, Targetable = true, CurrentTarget = true, X = 2 });
        var menu = new KtgAddonProbe { Name = "SystemMenu", Ready = true, Visible = true };
        snapshot.Addons.Add(menu);
        flow.Start(now);
        Assert(flow.Tick(snapshot, now.AddSeconds(3), false) == null, "menu recovery respects foreground");
        Assert(flow.Tick(snapshot, now.AddSeconds(3), true).Kind == "CloseSystemMenu",
            "visible system menu is closed before NPC interaction");
        Assert(flow.Tick(snapshot, now.AddSeconds(4), true) == null,
            "menu recovery waits for a fresh disappearance acknowledgement");
        menu.Visible = false;
        Assert(flow.Tick(snapshot, now.AddSeconds(5), true).Kind == "Interact",
            "closed system menu is not toggled open again");
        snapshot.Objects[0].CurrentTarget = false;
        Assert(flow.Tick(snapshot, now.AddSeconds(9), true).Kind == "TargetNpc",
            "normal targeting does not request Escape when menu is hidden");
        flow.Start(now);
        menu.Visible = true;
        flow.Tick(snapshot, now.AddSeconds(3), true);
        flow.Tick(snapshot, now.AddSeconds(5), true);
        flow.Tick(snapshot, now.AddSeconds(7), true);
        flow.Tick(snapshot, now.AddSeconds(9), true);
        Assert(!flow.Active, "failed menu recovery is bounded");
        Assert(GameInputSender.CloseObservedSystemMenu(delegate { return false; }) == GameInputResult.Cancelled,
            "cancelled menu recovery never emits Escape");
    }

    private static void TestSingleRun()
    {
        DateTime now = DateTime.UtcNow;
        var flow = new KeyboundSingleRun();
        var snapshot = new KtgMemorySnapshot { ProcessId = 1,
            AfkTimer = new KtgAfkTimerSnapshot { TerritoryTypeId = 1360 },
            LocalPlayer = new KtgObjectProbe { X = 0, Y = 0, Z = 0 } };
        var npc = new KtgObjectProbe { BaseId = 1058751, Name = "报名登记员", ObjectKind = 3,
            Targetable = true, X = 6 };
        snapshot.Objects.Add(npc);
        flow.Start(now);
        Assert(flow.Tick(snapshot, now.AddSeconds(3), true) == null, "flow waits outside NPC range");
        npc.X = 3;
        Assert(flow.Tick(snapshot, now.AddSeconds(4), false) == null, "flow waits for game foreground");
        Assert(flow.Tick(snapshot, now.AddSeconds(4), true).Kind == "TargetNpc", "flow targets nearby NPC");
        var otherNpc = new KtgObjectProbe { BaseId = 1058752, Name = "解说员", ObjectKind = 3,
            CurrentTarget = true, Targetable = true, X = 2 };
        snapshot.Objects.Add(otherNpc);
        Assert(flow.Tick(snapshot, now.AddSeconds(8), true).Kind == "TargetNpc",
            "nearest-NPC binding never interacts when a different NPC is selected");
        snapshot.Objects.Remove(otherNpc);
        flow.Start(now);
        npc.CurrentTarget = true;
        Assert(flow.Tick(snapshot, now.AddSeconds(8), true).Kind == "Interact", "flow interacts only after target acknowledged");
        var entry = new KtgAddonProbe { Name = "KTGEnterDialogue", Ready = true, Visible = true };
        entry.Buttons.Add(new KtgButtonProbe { NodeId = 10, Text = "取消", Enabled = true });
        entry.Buttons.Add(new KtgButtonProbe { NodeId = 11, Text = "参加", Enabled = true });
        snapshot.Addons.Add(entry);
        flow.Tick(snapshot, now.AddSeconds(9), true);
        var action = flow.Tick(snapshot, now.AddSeconds(11), true);
        Assert(action != null && action.Text == "参加", "flow selects registration button instead of cancel");
        snapshot.Addons.Clear();
        flow.Tick(snapshot, now.AddSeconds(12), true);
        Assert(flow.Stage == "Queue", "flow waits for queue after entry disappears");
        var confirm = new KtgAddonProbe { Name = "ContentsFinderConfirm", Ready = true, Visible = true };
        confirm.TextNodes.Add("49=魔光键影");
        confirm.Buttons.Add(new KtgButtonProbe { NodeId = 65, Text = "辞退", Enabled = true });
        confirm.Buttons.Add(new KtgButtonProbe { NodeId = 64, Text = "保留", Enabled = true });
        confirm.Buttons.Add(new KtgButtonProbe { NodeId = 63, Text = "出发", Enabled = false });
        snapshot.Addons.Add(confirm);
        Assert(flow.Tick(snapshot, now.AddSeconds(14), true) == null, "flow never clicks disabled confirm");
        confirm.Buttons[2].Enabled = true;
        var confirmAction = flow.Tick(snapshot, now.AddSeconds(15), true);
        Assert(confirmAction.Addon == "ContentsFinderConfirm" && confirmAction.NodeId == 63 &&
            confirmAction.Text == "出发", "flow selects actual CN departure button, never defer or withdraw");
        snapshot.Addons.Clear();
        snapshot.AfkTimer.TerritoryTypeId = 1359;
        snapshot.AddonCount = 1;
        flow.Tick(snapshot, now.AddSeconds(20), true);
        Assert(flow.InCombat, "flow enables combat after actual entry");
        var result = new KtgAddonProbe { Name = "KTGResult", Ready = true, Visible = true };
        result.Buttons.Add(new KtgButtonProbe { NodeId = 4, Text = "退出", Enabled = true });
        snapshot.Addons.Add(result);
        flow.Tick(snapshot, now.AddSeconds(30), true);
        Assert(!flow.InCombat && flow.Tick(snapshot, now.AddSeconds(32), true).Text == "退出", "flow exits only at result screen");
        snapshot.Addons.Clear();
        snapshot.AfkTimer.TerritoryTypeId = 1360;
        flow.Tick(snapshot, now.AddSeconds(34), true);
        Assert(!flow.Active && flow.Status.Contains("完成"), "flow finishes after returning without requeue");
        flow.Start(now);
        flow.Tick(snapshot, now.AddSeconds(130), true);
        Assert(!flow.Active && flow.Status.Contains("超时"), "flow has bounded stage timeout");
        flow.Start(now);
        flow.Stop("cancel");
        Assert(flow.Tick(snapshot, now.AddSeconds(4), true) == null, "cancel suppresses future actions");
        flow.Start(now);
        snapshot.ProcessId = 2;
        flow.Tick(snapshot, now.AddSeconds(3), true);
        snapshot.ProcessId = 3;
        flow.Tick(snapshot, now.AddSeconds(4), true);
        Assert(!flow.Active, "flow stops on process change");
        flow.Start(now);
        snapshot.Addons.Add(confirm);
        flow.Tick(snapshot, now.AddSeconds(3), true);
        snapshot.Addons.Add(entry);
        flow.Tick(snapshot, now.AddSeconds(4), true);
        snapshot.Addons.Remove(entry);
        confirm.TextNodes.Clear();
        confirm.TextNodes.Add("49=其他副本");
        flow.Tick(snapshot, now.AddSeconds(5), true);
        Assert(!flow.Active && flow.Status.Contains("不是魔光键影"), "flow rejects unrelated duty");
        Assert(GameInputSender.TargetRegistrationNpc(delegate { return false; }) == GameInputResult.Cancelled &&
            GameInputSender.InteractWithTarget(delegate { return false; }) == GameInputResult.Cancelled &&
            GameInputSender.ClickFlowButton(null, delegate { return false; }) == GameInputResult.Cancelled,
            "cancelled flow never emits keyboard or mouse input");
        snapshot.Addons.Clear();
        flow.Start(now);
        flow.Tick(snapshot, now.AddSeconds(3), true);
        flow.Tick(snapshot, now.AddSeconds(7), true);
        flow.Tick(snapshot, now.AddSeconds(11), true);
        flow.Tick(snapshot, now.AddSeconds(15), true);
        flow.Tick(snapshot, now.AddSeconds(19), true);
        Assert(!flow.Active, "NPC interaction retries are bounded");
    }

    private static void TestRepeatedFlow()
    {
        DateTime now = DateTime.UtcNow;
        var flow = new KeyboundSingleRun();
        var snapshot = new KtgMemorySnapshot { ProcessId = 1,
            AfkTimer = new KtgAfkTimerSnapshot { TerritoryTypeId = 1360 } };
        flow.Start(now, 2);
        EnterFlowCombat(flow, snapshot, now);
        Assert(flow.InCombat && flow.CompletedRuns == 0 && flow.TargetRuns == 2,
            "repeat flow starts first run without prematurely counting it");
        DateTime firstResult = now.AddMinutes(18);
        ExitFlowResult(flow, snapshot, firstResult);
        Assert(flow.Active && flow.Stage == "Npc" && flow.CompletedRuns == 1,
            "repeat flow returns to registration after acknowledged result exit");
        Assert(flow.Tick(snapshot, firstResult.AddSeconds(15), true) == null && flow.CompletedRuns == 1,
            "repeated lobby snapshots neither double count nor immediately requeue");
        // Long batches keep the timeout per run, rather than timing out after 30 total minutes.
        DateTime second = firstResult.AddSeconds(20);
        EnterFlowCombat(flow, snapshot, second);
        DateTime secondResult = now.AddMinutes(36);
        ExitFlowResult(flow, snapshot, secondResult);
        Assert(!flow.Active && flow.CompletedRuns == 2 && flow.Status.Contains("完成"),
            "repeat flow finishes exactly at configured run count");
        Assert(flow.Tick(snapshot, secondResult.AddSeconds(30), true) == null && flow.CompletedRuns == 2,
            "completed batch cannot start an extra run or count extra lobby samples");
        flow.Start(now, 3);
        EnterFlowCombat(flow, snapshot, now);
        snapshot.AfkTimer.TerritoryTypeId = 1360;
        flow.Tick(snapshot, now.AddSeconds(10), true);
        Assert(!flow.Active && flow.CompletedRuns == 0 && flow.Status.Contains("未经过结算"),
            "kick or early exit stops whole batch without counting or requeueing");
        flow.Start(now, 2);
        EnterFlowCombat(flow, snapshot, now);
        flow.Stop("用户中断");
        Assert(!flow.InCombat && flow.Tick(snapshot, now.AddSeconds(10), true) == null &&
            flow.CompletedRuns == 0, "interrupt during combat prevents every later flow action");
        flow.Start(now, 2);
        EnterFlowCombat(flow, snapshot, now);
        ExitFlowResult(flow, snapshot, now);
        flow.Stop("用户中断");
        Assert(flow.Tick(snapshot, now.AddSeconds(25), true) == null && flow.CompletedRuns == 1,
            "interrupt between runs preserves progress and cancels next registration");
        flow.Start(now, 2);
        EnterFlowCombat(flow, snapshot, now);
        snapshot.ProcessId = 2;
        flow.Tick(snapshot, now.AddSeconds(10), true);
        Assert(!flow.Active && flow.CompletedRuns == 0, "process changes stop repeated flow");
        foreach (int count in new[] { 0, 1000 })
        {
            bool rejected = false;
            try { flow.Start(now, count); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "repeat flow rejects out-of-range run count");
        }
        flow.Start(now, KeyboundSingleRun.MaximumRuns);
        Assert(flow.TargetRuns == 999 && flow.CompletedRuns == 0, "repeat flow supports configured maximum and resets progress");
        flow.Stop("end test");
    }

    private static void EnterFlowCombat(KeyboundSingleRun flow, KtgMemorySnapshot snapshot, DateTime now)
    {
        snapshot.Addons.Clear();
        snapshot.AfkTimer.TerritoryTypeId = 1360;
        snapshot.Addons.Add(new KtgAddonProbe { Name = "KTGEnterDialogue", Ready = true, Visible = true });
        flow.Tick(snapshot, now.AddSeconds(3), true);
        snapshot.Addons.Clear();
        flow.Tick(snapshot, now.AddSeconds(5), true);
        snapshot.AfkTimer.TerritoryTypeId = 1359;
        snapshot.AddonCount = 1;
        flow.Tick(snapshot, now.AddSeconds(8), true);
    }

    private static void ExitFlowResult(KeyboundSingleRun flow, KtgMemorySnapshot snapshot, DateTime now)
    {
        var result = new KtgAddonProbe { Name = "KTGResult", Ready = true, Visible = true };
        result.Buttons.Add(new KtgButtonProbe { NodeId = 48, Text = "退出", Enabled = true });
        snapshot.Addons.Add(result);
        flow.Tick(snapshot, now.AddSeconds(10), true);
        flow.Tick(snapshot, now.AddSeconds(12), true);
        int before = flow.CompletedRuns;
        snapshot.AfkTimer.TerritoryTypeId = 1360;
        flow.Tick(snapshot, now.AddSeconds(13), true);
        Assert(flow.CompletedRuns == before, "result still visible cannot count completion");
        snapshot.Addons.Clear();
        flow.Tick(snapshot, now.AddSeconds(14), true);
    }


    private static void TestActorCastPacket()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeyboundOpcode-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "OpCode.txt");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "ActorControl|123\r\nActorCast|1c9\r\n");
            Assert(PacketMetadata.LoadOpcode(path, "ActorCast") == 0x1C9,
                "ActorCast opcode loading");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }

        byte[] packet = new byte[64];
        Array.Copy(BitConverter.GetBytes((uint)packet.Length), 0, packet, 0, 4);
        Array.Copy(BitConverter.GetBytes(0x40001234u), 0, packet, 4, 4);
        Array.Copy(BitConverter.GetBytes((ushort)0x1C9), 0, packet, PacketMetadata.OpcodeOffset, 2);
        Array.Copy(BitConverter.GetBytes((ushort)KeyboundCombatPlanner.CurseGazeAction), 0,
            packet, PacketMetadata.ActorCastActionOffset, 2);
        Array.Copy(BitConverter.GetBytes(4.7f), 0, packet, PacketMetadata.ActorCastTimeOffset, 4);
        uint actorId;
        uint actionId;
        double castSeconds;
        Assert(PacketMetadata.TryReadActorCast(packet, 0x1C9, out actorId, out actionId,
            out castSeconds) && actorId == 0x40001234u &&
            actionId == KeyboundCombatPlanner.CurseGazeAction && Math.Abs(castSeconds - 4.7) < 0.001,
            "raw ActorCast packet decoding");
        Assert(!PacketMetadata.TryReadActorCast(packet, 0x1CA, out actorId, out actionId,
            out castSeconds), "raw ActorCast rejects another opcode");
    }

    private static void TestKtgMemoryLayout()
    {
        Assert(KtgProcessMemoryReader.KtgeWordEntriesOffset == 0x238,
            "KTGEWord entries offset");
        Assert(KtgProcessMemoryReader.KtgeWordEntrySize == 0xC8 &&
            KtgProcessMemoryReader.KtgeWordEntryCount == 20,
            "KTGEWord entry layout");
        Assert(KtgProcessMemoryReader.AtkUnitBaseUldManagerOffset == 0x28 &&
            KtgProcessMemoryReader.AtkUldManagerNodeListCountOffset == 0x42 &&
            KtgProcessMemoryReader.AtkUldManagerNodeListOffset == 0x50,
            "AtkUldManager node list layout");
        Assert(KtgProcessMemoryReader.KtgInputNodeId == 7 &&
            KtgProcessMemoryReader.AtkResNodeIdOffset == 0x8 &&
            KtgProcessMemoryReader.AtkComponentNodeTypeMinimum == 1000 &&
            KtgProcessMemoryReader.AtkComponentNodeComponentOffset == 0xC0 &&
            KtgProcessMemoryReader.AtkComponentInputBaseRawStringOffset == 0x148 &&
            KtgProcessMemoryReader.AtkComponentInputBaseOwnerAddonOffset == 0x1B0,
            "KTGInput component layout");
        Assert(KtgProcessMemoryReader.InputTimerAfkTimerOffset == 0x10 &&
            KtgProcessMemoryReader.InputTimerContentInputTimerOffset == 0x14 &&
            KtgProcessMemoryReader.InputTimerInputTimerOffset == 0x18 &&
            KtgProcessMemoryReader.InputTimerAutoAfkTimeLimitOffset == 0x20 &&
            KtgProcessMemoryReader.InputTimerInstanceContentAfkTimeLimitOffset == 0x28 &&
            KtgProcessMemoryReader.InputTimerPvpAfkTimeLimitOffset == 0x2C &&
            KtgProcessMemoryReader.InputTimerMjiAfkTimeLimitOffset == 0x30 &&
            KtgProcessMemoryReader.InputTimerAfkTimeLimitOffset == 0x38 &&
            KtgProcessMemoryReader.InputTimerStatusOffset == 0x3C &&
            KtgProcessMemoryReader.InputTimerTerritoryTypeIdOffset == 0x42,
            "InputTimerModule layout");
        byte[] data = { 0x00, 0x48, 0x8B, 0x05, 0x11, 0x22, 0x33, 0x44, 0x4C, 0x8B };
        byte[] pattern = { 0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x4C, 0x8B };
        bool[] mask = { true, true, true, false, false, false, false, true, true };
        Assert(KtgProcessMemoryReader.FindPattern(data, pattern, mask) == 1,
            "signature wildcard scan");
        using (var reader = new KtgProcessMemoryReader())
        {
            KtgMemorySnapshot snapshot = reader.ReadSnapshot(true);
            Assert(snapshot != null && !String.IsNullOrEmpty(snapshot.Status),
                "memory reader reports status without game");
        }
    }

    private static void TestDirectPromptCompletionTracker()
    {
        var tracker = new DirectPromptCompletionTracker();
        const uint entity = 0x40001234;
        tracker.MarkCompleted(entity, "重复长句");
        Assert(tracker.IsSuppressed(entity, "重复长句"),
            "completed prompt is suppressed while unchanged");
        Assert(tracker.Observe(entity, "另一道题") &&
            !tracker.IsSuppressed(entity, "重复长句"),
            "prompt change releases prior completion");
        tracker.MarkCompleted(entity, "另一道题");
        tracker.RemoveEntity(entity);
        Assert(!tracker.IsSuppressed(entity, "另一道题"),
            "entity removal clears prompt completion");
    }

    private static void TestCombatPlanner()
    {
        var planner = new KeyboundCombatPlanner();
        planner.SetDirectorPhase(1);
        planner.SetDirectorPhase(2);
        Assert(planner.CombatStage == 1 && planner.CanAttack,
            "director active phase starts semantic P1 and permits attacks");
        Assert(!KeyboundCombatPlanner.CanUsePrompt(true, false) &&
            KeyboundCombatPlanner.CanUsePrompt(true, true) &&
            KeyboundCombatPlanner.CanUsePrompt(false, false),
            "obstructed prompts are only eligible during confirmed visual interference");
        planner.ObserveTarget(0x40000020);
        planner.ObserveTarget(0x40000010);
        planner.ObserveTarget(0x40000020);
        Assert(planner.GetAppearanceOrder(0x40000020) == 1 &&
            planner.GetAppearanceOrder(0x40000010) == 2,
            "P1 appearance order is stable and deduplicated");
        Assert(planner.CompareTargets(0x40000020, 9, 0x40000010, 0, 0,
            DateTime.UtcNow) < 0,
            "P1 earlier target wins over a nearer slot");

        Assert(planner.MarkDied(0x40000020) && !planner.MarkDied(0x40000020) &&
            planner.PhaseOneKillCount == 1,
            "P1 duplicate death only counts once");
        for (int index = 1; index < KeyboundCombatPlanner.PhaseOneKillGoal; index++)
            planner.MarkDied((uint)(0x41000000 + index));
        Assert(planner.IsPhaseOneComplete && planner.PhaseOneKillCount == 25 &&
            planner.CombatStage == 2, "P1 completes at 25 kills and advances to P2");

        const uint normal = 0x42000001;
        const uint reward = 0x42000002;
        const uint missile = 0x42000003;
        const uint cactuar = 0x42000004;
        const uint shield = 0x42000005;
        const uint behemoth = 0x42000006;
        const uint meteor = 0x42000007;
        const uint barrier = 0x42000008;
        planner.ObserveActor(normal, 0x4C82);
        planner.ObserveActor(reward, KeyboundCombatPlanner.RewardType);
        planner.ObserveActor(missile, KeyboundCombatPlanner.MissileType);
        planner.ObserveActor(cactuar, KeyboundCombatPlanner.CactuarType);
        planner.ObserveActor(shield, KeyboundCombatPlanner.ShieldType);
        planner.ObserveActor(behemoth, KeyboundCombatPlanner.BehemothType);
        planner.ObserveActor(meteor, KeyboundCombatPlanner.BehemothMeteorType);
        planner.ObserveActor(barrier, KeyboundCombatPlanner.BehemothBarrierType);
        planner.ObserveTarget(normal);
        planner.ObserveTarget(reward);
        planner.ObserveTarget(missile);
        planner.ObserveTarget(cactuar);
        planner.ObserveTarget(shield);
        planner.ObserveTarget(behemoth);
        planner.ObserveTarget(meteor);
        planner.ObserveTarget(barrier);
        DateTime now = DateTime.UtcNow;
        Assert(planner.CompareTargets(reward, 9, missile, 0, normal, now) < 0,
            "P2 reward target outranks missile and current normal target");
        planner.RecordCastStart(cactuar, KeyboundCombatPlanner.CactuarNeedleAction, 6.7, now);
        Assert(planner.CompareTargets(cactuar, 9, reward, 0, normal, now) < 0,
            "P2 four-hit cast outranks reward target");
        Assert(planner.CompareTargets(shield, 9, cactuar, 0, normal, now) < 0,
            "shield is absolute highest priority");
        Assert(planner.CompareTargets(barrier, 9, cactuar, 0, behemoth, now) < 0 &&
            planner.CompareTargets(meteor, 9, behemoth, 0, behemoth, now) < 0,
            "P2 Behemoth barrier and meteor preempt boss body");

        const uint storm = 0x43000001;
        const uint mine = 0x43000002;
        const uint stormMissile = 0x43000003;
        planner.ObserveActor(storm, KeyboundCombatPlanner.StormDestroyerType);
        planner.ObserveActor(mine, KeyboundCombatPlanner.MineType);
        planner.ObserveActor(stormMissile, KeyboundCombatPlanner.StormMissileType);
        planner.ObserveTarget(storm);
        planner.ObserveTarget(mine);
        planner.ObserveTarget(stormMissile);
        Assert(planner.CombatStage == 3 &&
            planner.CompareTargets(mine, 1, storm, 0, storm, now) < 0,
            "P3 mine preempts focused boss");
        Assert(planner.CompareTargets(stormMissile, 9, storm, 0, storm, now) < 0,
            "P3 missile preempts focused boss");
        planner.RecordCastStart(storm, KeyboundCombatPlanner.ThunderBombAction, 11.7, now);
        Assert(planner.CompareTargets(storm, 9, mine, 0, mine, now) < 0,
            "P3 thunder bomb four-hit cast preempts mine");

        const uint eye = 0x44000001;
        DateTime castStart = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        planner.RecordCastStart(eye, KeyboundCombatPlanner.CurseGazeAction, 4.7, castStart);
        Assert(!planner.ShouldPause(castStart.AddSeconds(1.4)) &&
            planner.ShouldPause(castStart.AddSeconds(1.5)) &&
            planner.ShouldPause(castStart.AddSeconds(6.6)) &&
            !planner.ShouldPause(castStart.AddSeconds(6.8)),
            "gaze pauses within 1.5 seconds of cast start through two seconds after expected end");
        planner.RecordCastStart(eye, KeyboundCombatPlanner.CurseGazeAction, 4.7, castStart);
        planner.RecordCastEnd(eye, KeyboundCombatPlanner.CurseGazeAction, true,
            castStart.AddSeconds(4.2));
        Assert(!planner.ShouldPause(castStart.AddSeconds(4.2)),
            "interrupted stop cast clears pause window");

        planner.RecordCastStart(eye, KeyboundCombatPlanner.CurseGazeAction, 4.7, castStart);
        planner.RecordCastEnd(eye, KeyboundCombatPlanner.CurseGazeAction, false,
            castStart.AddSeconds(5));
        Assert(planner.ShouldPause(castStart.AddSeconds(6.9)) &&
            !planner.ShouldPause(castStart.AddSeconds(7.1)),
            "confirmed stop mechanic keeps input paused for two seconds after resolution");

        foreach (uint stopAction in new[] {
            KeyboundCombatPlanner.CurseGazeAction,
            KeyboundCombatPlanner.MotionDetectorScorpionAction,
            KeyboundCombatPlanner.MotionDetectorStormAction })
        {
            Assert(KeyboundCombatPlanner.IsStopCastAction(stopAction),
                "all P2/P3 stop casts use the fast ActorCast path: 0x" + stopAction.ToString("X"));
            Assert(KeyboundCombatPlanner.ResolveCastStartTime(stopAction,
                castStart.AddSeconds(-3), castStart) == castStart,
                "stop cast timing ignores stale ACT event timestamp: 0x" + stopAction.ToString("X"));
        }
        Assert(KeyboundCombatPlanner.ResolveCastStartTime(
            KeyboundCombatPlanner.ThunderBombAction, castStart.AddSeconds(-3), castStart) ==
            castStart.AddSeconds(-3), "ordinary priority cast keeps event timestamp");

        planner.SetDirectorPhase(4);
        Assert(planner.PhaseOneKillCount == 0 && planner.TrackedTargetCount == 0 &&
            planner.CombatStage == 0 && !planner.CanAttack,
            "challenge completion resets semantic stage and blocks attacks");
    }

    private static void TestCombatLogParser()
    {
        var parser = new KeyboundCombatLogParser();
        KeyboundCombatSignal signal;
        Assert(parser.TryParse(
            "261|2026-10-29T02:53:07.6340000+08:00|Add|4000AE60|BNpcID|4C89|BNpcNameID|6C|Level|100|MaxHP|3700|hash",
            out signal) && signal.Kind == KeyboundCombatSignalKind.ActorAdded &&
            signal.EntityId == 0x4000AE60 && signal.BNpcId == 0x4C89 && signal.MaxHp == 3700,
            "combat parser actor add");
        Assert(parser.TryParse(
            "20|2026-10-29T02:54:42.8380000+08:00|4000AE60||C17C|运动体探知干扰器|4000AE60||4.700|||||hash",
            out signal) && signal.Kind == KeyboundCombatSignalKind.CastStarted &&
            signal.ActionId == 0xC17C && Math.Abs(signal.CastSeconds - 4.7) < 0.001,
            "combat parser cast start");
        Assert(parser.TryParse(
            "23|2026-10-29T02:04:58.7600000+08:00|40005CDD|暴风破坏者|C223|破坏加农炮|Interrupted|hash",
            out signal) && signal.Kind == KeyboundCombatSignalKind.CastEnded && signal.Interrupted,
            "combat parser cast interruption");
        Assert(parser.TryParse(
            "22|2026-10-29T21:56:20.9570000+08:00|4000B58E|暴风破坏者|C226|运动体探知干扰器|1001|玩家|hash",
            out signal) && signal.Kind == KeyboundCombatSignalKind.CastEnded &&
            !signal.Interrupted && signal.ActionId == 0xC226,
            "combat parser stop mechanic resolution");
        Assert(parser.TryParse(
            "26|2026-10-29T02:01:17.7680000+08:00|14E4|视觉妨碍|28.00|40005C98||1001CFAD|玩家|493|168335||hash",
            out signal) && signal.Kind == KeyboundCombatSignalKind.StatusAdded &&
            signal.StatusId == 0x14E4 && signal.SourceId == 0x40005C98 &&
            signal.TargetId == 0x1001CFAD && Math.Abs(signal.DurationSeconds - 28) < 0.001,
            "combat parser visual interference status");
    }

    private static void TestActSettings()
    {
        var settings = new KeyboundActSettings();
        Assert(settings.AutomationRunCount == 1, "automation count defaults to one run");
        Assert(settings.HumanHotkeyKey == (int)System.Windows.Forms.Keys.F1 &&
            settings.WarriorHotkeyKey == (int)System.Windows.Forms.Keys.F2 &&
            settings.CustomHotkeyKey == (int)System.Windows.Forms.Keys.F3 &&
            settings.HumanHotkeyModifiers == 0 && settings.WarriorHotkeyModifiers == 0 &&
            settings.CustomHotkeyModifiers == 0,
            "mode hotkeys default to F1 F2 F3");
        Assert(new KeyboundHotkeyBinding((int)System.Windows.Forms.Keys.F1,
            KeyboundModeHotkeyManager.ModifierControl | KeyboundModeHotkeyManager.ModifierShift)
            .DisplayText == "Ctrl+Shift+F1", "mode hotkey display text");
        using (var hotkeys = new KeyboundModeHotkeyManager())
        {
            string hotkeyError = hotkeys.Register(
                new KeyboundHotkeyBinding((int)System.Windows.Forms.Keys.F21, 0),
                new KeyboundHotkeyBinding((int)System.Windows.Forms.Keys.F22, 0),
                new KeyboundHotkeyBinding((int)System.Windows.Forms.Keys.F23, 0));
            Assert(String.IsNullOrEmpty(hotkeyError), "mode global hotkeys register and release");
        }
        KeyboundTimingProfile humanProfile = settings.GetTimingProfile("一二三四五六");
        Assert(humanProfile.ReactionDelayMs == 150 && humanProfile.CharacterDelayMs == 70 &&
            humanProfile.SubmitDelayMs == 150 && humanProfile.TotalMinimumMs == 1000 &&
            humanProfile.TotalMaximumMs == 3000 && humanProfile.JitterPercent == 20,
            "human preset values");
        Assert(settings.GetTimingProfile("一二三四五六七").TotalMinimumMs == 2000,
            "human preset switches minimum after six characters");
        KeyboundTimingPlan normal = settings.CreateTimingPlan("第捌区车站", new Random(7));
        Assert(normal.Mode == KeyboundActSettings.HumanMode &&
            normal.PlannedTotalMs >= normal.TotalMinimumMs &&
            normal.PlannedTotalMs <= normal.TotalMaximumMs && normal.CharacterDelaysMs.Length == 5,
            "human preset uses bounded per-character cadence");
        KeyboundTimingPlan longHuman = settings.CreateTimingPlan("超过六个字的长题目", new Random(7));
        Assert(longHuman.TotalMinimumMs >= 1600 && longHuman.TotalMinimumMs <= 2400 &&
            longHuman.PlannedTotalMs >= longHuman.TotalMinimumMs &&
            longHuman.PlannedTotalMs <= longHuman.TotalMaximumMs,
            "human long prompt raises and jitters total minimum");
        settings.TimingMode = KeyboundActSettings.WarriorMode;
        KeyboundTimingProfile warriorProfile = settings.GetTimingProfile("任意题目");
        Assert(warriorProfile.ReactionDelayMs == 50 && warriorProfile.CharacterDelayMs == 30 &&
            warriorProfile.SubmitDelayMs == 50 && warriorProfile.TotalMinimumMs == 500 &&
            warriorProfile.TotalMaximumMs == 500 && warriorProfile.JitterPercent == 0,
            "warrior preset values");
        KeyboundTimingPlan warrior = settings.CreateTimingPlan("第捌区车站", new Random(7));
        Assert(warrior.PlannedTotalMs == 500 && warrior.TotalMinimumMs == 500 &&
            warrior.TotalMaximumMs == 500, "warrior preset has fixed 500 ms total");

        var conflicting = new KeyboundActSettings
        {
            TimingMode = KeyboundActSettings.CustomMode,
            CustomReactionDelayMs = 100,
            CustomCharacterDelayMs = 100,
            CustomSubmitDelayMs = 100,
            CustomTotalMinimumMs = 3000,
            CustomTotalMaximumMs = 3100,
            CustomJitterPercent = 80
        };
        for (int seed = 0; seed < 100; seed++)
        {
            KeyboundTimingPlan jittered = conflicting.CreateTimingPlan("抖动测试", new Random(seed));
            Assert(jittered.TotalMinimumMs <= jittered.TotalMaximumMs &&
                jittered.PlannedTotalMs >= jittered.TotalMinimumMs &&
                jittered.PlannedTotalMs <= jittered.TotalMaximumMs,
                "jittered total bounds never conflict");
        }

        string directory = Path.Combine(Path.GetTempPath(), "KeyboundActSettings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.ini");
        try
        {
            settings.TimingMode = KeyboundActSettings.CustomMode;
            settings.CustomReactionDelayMs = 240;
            settings.CustomCharacterDelayMs = 135;
            settings.CustomSubmitDelayMs = 180;
            settings.CustomTotalMinimumMs = 2500;
            settings.CustomTotalMaximumMs = 2000;
            settings.CustomJitterPercent = 12;
            settings.HumanHotkeyKey = (int)System.Windows.Forms.Keys.F6;
            settings.HumanHotkeyModifiers = KeyboundModeHotkeyManager.ModifierControl;
            settings.WarriorHotkeyKey = (int)System.Windows.Forms.Keys.F7;
            settings.WarriorHotkeyModifiers = KeyboundModeHotkeyManager.ModifierAlt;
            settings.CustomHotkeyKey = (int)System.Windows.Forms.Keys.F8;
            settings.CustomHotkeyModifiers = KeyboundModeHotkeyManager.ModifierShift;
            settings.AutomationRunCount = 12;
            settings.Save(path);
            KeyboundActSettings loaded = KeyboundActSettings.Load(path);
            Assert(loaded.TimingMode == KeyboundActSettings.CustomMode &&
                loaded.CustomReactionDelayMs == 240 && loaded.CustomCharacterDelayMs == 135 &&
                loaded.CustomSubmitDelayMs == 180 && loaded.CustomTotalMinimumMs == 2500 &&
                loaded.CustomTotalMaximumMs == 2500 && loaded.CustomJitterPercent == 12 &&
                loaded.HumanHotkeyKey == (int)System.Windows.Forms.Keys.F6 &&
                loaded.HumanHotkeyModifiers == KeyboundModeHotkeyManager.ModifierControl &&
                loaded.WarriorHotkeyKey == (int)System.Windows.Forms.Keys.F7 &&
                loaded.WarriorHotkeyModifiers == KeyboundModeHotkeyManager.ModifierAlt &&
                loaded.CustomHotkeyKey == (int)System.Windows.Forms.Keys.F8 &&
                loaded.CustomHotkeyModifiers == KeyboundModeHotkeyManager.ModifierShift,
                "ACT cadence settings round trip");
            Assert(loaded.AutomationRunCount == 12, "automation count persists independently of timing settings");
            foreach (int count in new[] { 0, 1000 })
            {
                File.WriteAllText(path, "AutomationRunCount=" + count);
                loaded = KeyboundActSettings.Load(path);
                Assert(loaded.AutomationRunCount >= 1 && loaded.AutomationRunCount <= 999,
                    "persisted automation count is bounded");
            }

            File.WriteAllLines(path, new[] {
                "Humanize=False", "BlatantMode=False", "StartDelayMinMs=100",
                "StartDelayMaxMs=200", "CharacterDelayMs=45", "JitterPercent=15",
                "SubmitDelayMinMs=120", "SubmitDelayMaxMs=180",
                "TypingSubmitMinimumMs=900", "TypingSubmitBudgetMs=2200",
                "TargetOrder=longest"
            });
            loaded = KeyboundActSettings.Load(path);
            Assert(loaded.TimingMode == KeyboundActSettings.CustomMode &&
                loaded.CustomReactionDelayMs == 150 && loaded.CustomCharacterDelayMs == 45 &&
                loaded.CustomSubmitDelayMs == 150 && loaded.CustomTotalMinimumMs == 900 &&
                loaded.CustomTotalMaximumMs == 2200 && loaded.CustomJitterPercent == 15,
                "legacy cadence settings migrate to custom profile");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void TestDalamudLauncherGuards()
    {
        var launcher = new DalamudAutoLauncher(Path.GetTempPath());
        launcher.Poll(false, false);
        Assert(launcher.Status == "已停用", "minimal Dalamud injection opt-out");
        launcher.Poll(true, true);
        Assert(launcher.Status == "Dalamud 桥接已连接", "connected bridge suppresses injection");
    }

    private static void TestBridgeProtocol()
    {
        KeyboundBridgeSnapshot snapshot = null;
        KeyboundBridgeSubmitResult submitResult = null;
        using (var client = new KeyboundBridgeClient("unit-test"))
        {
            client.SnapshotReceived += delegate(KeyboundBridgeSnapshot value)
            {
                snapshot = value;
            };
            client.SubmitResultReceived += delegate(KeyboundBridgeSubmitResult value)
            {
                submitResult = value;
            };

            client.ProcessIncomingLine("STATE|7|1359|1|1|1|1");
            client.ProcessIncomingLine("TARGET|7|40001234|2|0|" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("第捌区车站")));
            client.ProcessIncomingLine("END|7");
            Assert(snapshot != null && snapshot.TerritoryId == 1359 && snapshot.InputReady,
                "bridge state parsed");
            Assert(snapshot != null && snapshot.Targets.Count == 1 &&
                snapshot.Targets[0].EntityId == 0x40001234 && snapshot.Targets[0].Text == "第捌区车站",
                "bridge target parsed");

            client.ProcessIncomingLine("RESULT|req-1|OK|40001234|" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("accepted")));
            Assert(submitResult != null && submitResult.Success && submitResult.Message == "accepted",
                "bridge result parsed");
        }
    }

    private static void TestGameInputGuards()
    {
        Assert(GameInputSender.IsFfxivProcessName("ffxiv_dx11"), "dx11 process accepted");
        Assert(GameInputSender.IsFfxivProcessName("FFXIV"), "dx9 process accepted");
        Assert(!GameInputSender.IsFfxivProcessName("notepad"), "other process rejected");
        Assert(GameInputSender.NormalizeChineseAnswer("克劳德·斯特莱夫！") == "克劳德斯特莱夫",
            "Chinese answer punctuation removed");
        Assert(GameInputSender.NormalizeChineseAnswer("まこう") == String.Empty,
            "non-Chinese answer rejected");
        Assert(GameInputSender.NormalizePinyinKeys("LüYeZhiJia") == "lvyezhijia",
            "pinyin keys normalize umlaut");
        Assert(GameInputSender.NormalizePinyinKeys("まこう") == String.Empty,
            "non-pinyin keys rejected");
        Assert(GameInputSender.NormalizeFullPinyinKeys("KeLaoDe") == "ke'lao'de",
            "full pinyin syllables separated");
        Assert(GameInputSender.NormalizeFullPinyinKeys("LüYeZhiJia") == "lv'ye'zhi'jia",
            "full pinyin umlaut normalized");
        Assert(GameInputSender.NativeInputSize == (IntPtr.Size == 8 ? 40 : 28),
            "native INPUT layout size");
        Assert(GameInputSender.SendUnicode(String.Empty, 0) == GameInputResult.EmptyText,
            "empty input is not sent");
    }

    private static void TestOverlayServer()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeyboundOverlay-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool requested = false;
            int flowStopRequests = 0;
            using (var server = new KeyboundOverlayServer(directory))
            {
                Assert(server.Port > 0 && String.IsNullOrEmpty(server.Error), "overlay loopback server starts");
                string runtime = File.ReadAllText(Path.Combine(directory, "runtime.js"));
                const string marker = "token:\"";
                int start = runtime.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                int end = runtime.IndexOf('"', start);
                string token = runtime.Substring(start, end - start);
                server.SetState("{\"Version\":1,\"Ready\":true}");
                server.AutomationRequested += delegate(bool enabled) { requested = enabled; };
                server.FlowStopRequested += delegate { flowStopRequests++; };
                using (var client = new WebClient())
                {
                    client.Proxy = null;
                    string root = "http://127.0.0.1:" + server.Port;
                    string state = client.DownloadString(root + "/state?token=" + token);
                    Assert(state.IndexOf("\"Ready\":true", StringComparison.Ordinal) >= 0,
                        "overlay state endpoint");
                    client.UploadData(root + "/automation?enabled=true&token=" + token,
                        "POST", new byte[0]);
                    Assert(requested, "overlay automation endpoint");
                    client.UploadData(root + "/flow/stop?token=" + token, "POST", new byte[0]);
                    Assert(flowStopRequests == 1, "overlay interrupt dispatches flow stop");
                    try { client.UploadData(root + "/flow/stop?token=wrong", "POST", new byte[0]); }
                    catch (WebException ex)
                    { using (var response = (HttpWebResponse)ex.Response)
                        Assert(response.StatusCode == HttpStatusCode.Forbidden, "flow stop requires runtime token"); }
                    try { client.DownloadString(root + "/flow/stop?token=" + token); }
                    catch (WebException ex)
                    { using (var response = (HttpWebResponse)ex.Response)
                        Assert(response.StatusCode == HttpStatusCode.NotFound, "GET cannot interrupt a flow"); }
                    Assert(flowStopRequests == 1, "unauthorized or read-only requests never dispatch stop");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Overlay test exception: " + ex.GetType().FullName + ": " + ex.Message);
            Assert(false, "overlay loopback request completes");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void TestDecoder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeyboundWords-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "words.tsv");
        File.WriteAllText(path, "86\t不会屈服于胁迫\tBuHuiQuFuYuXiePo\n");
        try
        {
            KeyboundWordTable table = KeyboundWordTable.Load(path);
            var decoder = new KeyboundLogDecoder(table);
            KeyboundEvent value;
            Assert(decoder.TryDecode(0x33,
                "33|2026-10-28T19:21:08.749+08:00|8003DEA9|10000008|400021DF|00|4C4B4A|56|hash",
                out value), "prompt decodes");
            Assert(value.Kind == KeyboundEventKind.Prompt, "prompt kind");
            Assert(value.MonsterId == 0x400021DF, "prompt monster");
            Assert(value.WordId == 0x56, "prompt word id");
            Assert(value.Word != null && value.Word.InputText == "BuHuiQuFuYuXiePo", "prompt word lookup");
            Assert(value.Word != null && value.Word.InputKeys == "bhqfyxp", "prompt initial keys");
            KeyboundWord reverse = table.FindByDisplayText("不会屈服于胁迫！");
            Assert(reverse != null && reverse.InputText == "BuHuiQuFuYuXiePo",
                "display text resolves full pinyin despite optional punctuation");
            KeyboundWord randomWrong = table.GetRandomDifferent("不会屈服于胁迫", new Random(1));
            Assert(randomWrong == null,
                "single-entry word table does not reuse the current prompt as a wrong answer");
            Assert(decoder.TryDecode(0x33,
                "8003DEA9|10000016|400021DF|BFC|00|00", out value) &&
                value.Kind == KeyboundEventKind.InputSuccess, "success decodes");
            Assert(decoder.TryDecode(0x33,
                "8003DEA9|1000000B|400021DF|00|00|00", out value) &&
                value.Kind == KeyboundEventKind.MonsterDied, "death decodes");
            Assert(!decoder.TryDecode(0x33,
                "80000000|10000008|400021DF|00|4C4B4A|56", out value), "other director ignored");
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }

    private static void TestHeader()
    {
        byte[] packet = BuildPacket(0x4321, 0x10203040, "hello-keybound");
        PacketHeader header;
        Assert(PacketMetadata.TryReadHeader(packet, out header), "header should parse");
        Assert(header.Opcode == 0x4321, "opcode");
        Assert(header.ActorId == 0x10203040, "actor");
        Assert(PacketMetadata.ExtractPrintable(packet).Contains("hello-keybound"), "printable text");
        Assert(PacketMetadata.Fingerprint(packet) == PacketMetadata.Fingerprint(packet), "stable fingerprint");
    }

    private static void TestCapture()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KeyboundProbeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var capture = new ProbeCaptureSession(directory);
            string binary = capture.BinaryPath;
            string events = capture.EventPath;
            string summary = capture.SummaryPath;
            string memory = capture.MemoryPath;
            capture.Record("zone", 123, BuildPacket(0x1201, 7, "prompt-alpha"));
            capture.Record("zone", 124, BuildPacket(0x1202, 8, "success"));
            capture.Mark("PROMPT_APPEARED");
            capture.RecordMemory("AFK", "afk=12.500\tcontent=7.000\nclean");
            capture.Stop();

            Assert(File.Exists(binary), "binary exists");
            Assert(File.Exists(events), "events exists");
            Assert(File.Exists(summary), "summary exists");
            Assert(File.Exists(memory), "memory probe log exists");
            using (var reader = new BinaryReader(File.OpenRead(binary), Encoding.UTF8))
            {
                Assert(Encoding.ASCII.GetString(reader.ReadBytes(4)) == "KBP1", "binary magic");
                Assert(reader.ReadInt32() == 1, "binary version");
                reader.ReadInt64();
                Assert(reader.ReadInt32() == 1, "binary part number");
            }
            string eventText = File.ReadAllText(events);
            string summaryText = File.ReadAllText(summary);
            string memoryText = File.ReadAllText(memory);
            Assert(eventText.Contains("0x1201"), "packet event");
            Assert(eventText.Contains("PROMPT_APPEARED"), "marker event");
            Assert(summaryText.Contains("0x1202"), "summary opcode");
            Assert(memoryText.Contains("\tAFK\tafk") &&
                memoryText.Contains("afk=12.500 content=7.000 clean"),
                "memory probe record and single-line escaping");
            capture.Dispose();
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }

    private static byte[] BuildPacket(ushort opcode, uint actor, string payload)
    {
        byte[] text = Encoding.ASCII.GetBytes(payload);
        byte[] packet = new byte[PacketMetadata.HeaderSize + text.Length + 1];
        Buffer.BlockCopy(BitConverter.GetBytes(packet.Length), 0, packet, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(actor), 0, packet, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, packet, PacketMetadata.OpcodeOffset, 2);
        Buffer.BlockCopy(text, 0, packet, PacketMetadata.HeaderSize, text.Length);
        return packet;
    }

    private static void Assert(bool condition, string name)
    {
        if (condition)
            return;
        failures++;
        Console.Error.WriteLine("FAIL: " + name);
    }
}
