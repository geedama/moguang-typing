using System;
using System.Collections.Generic;

namespace ActEnhanced.KeyboundProbe
{
    internal sealed class KeyboundFlowAction
    {
        public string Kind;
        public string Addon;
        public uint NodeId;
        public string Text;
    }

    // Pure state machine: every irreversible UI step waits for an observed acknowledgement.
    internal sealed class KeyboundSingleRun
    {
        internal const uint RegistrationNpcBaseId = 1058751;
        internal const float InteractionRange = 5;
        internal const int MaximumRuns = 999;
        private DateTime startedUtc;
        private DateTime stageUtc;
        private DateTime nextActionUtc;
        private int attempts;
        private int menuCloseAttempts;
        private int processId;
        public bool Active { get; private set; }
        public string Stage { get; private set; }
        public string Status { get; private set; }
        public bool InCombat { get { return Active && Stage == "Combat"; } }
        public int TargetRuns { get; private set; }
        public int CompletedRuns { get; private set; }
        public string Progress { get { return "已完成 " + CompletedRuns + "/" + TargetRuns + " 局"; } }

        public void Start(DateTime now)
        {
            Start(now, 1);
        }

        public void Start(DateTime now, int targetRuns)
        {
            if (targetRuns < 1 || targetRuns > MaximumRuns)
                throw new ArgumentOutOfRangeException("targetRuns");
            Active = true;
            TargetRuns = targetRuns;
            CompletedRuns = 0;
            processId = 0;
            menuCloseAttempts = 0;
            startedUtc = now;
            SetStage("Npc", "等待靠近报名登记员（5 米内）", now);
            nextActionUtc = now.AddSeconds(2);
        }

        public void Stop(string reason)
        {
            Active = false;
            Stage = "Stopped";
            Status = reason;
        }

        private void SetStage(string stage, string status, DateTime now)
        {
            Stage = stage;
            Status = status;
            stageUtc = now;
            attempts = 0;
            nextActionUtc = now.AddSeconds(1);
        }

        internal static KtgAddonProbe FindAddon(KtgMemorySnapshot snapshot, string name)
        {
            foreach (KtgAddonProbe addon in snapshot.Addons)
                if (addon.Name == name && addon.Ready && addon.Visible) return addon;
            return null;
        }

        internal static bool IsNearby(KtgObjectProbe player, KtgObjectProbe npc)
        {
            if (player == null || npc == null) return false;
            double dx = player.X - npc.X, dy = player.Y - npc.Y, dz = player.Z - npc.Z;
            double distance = dx * dx + dy * dy + dz * dz;
            return !Double.IsNaN(distance) && distance <= InteractionRange * InteractionRange;
        }

        internal static bool IsAllowedButton(string addon, string text)
        {
            if (addon == "KTGEnterDialogue")
                return text == "参加" || text == "申请参加" || text == "报名";
            if (addon == "ContentsFinderConfirm") return text == "出发" || text == "开始" || text == "进入";
            if (addon == "KTGResult") return text == "退出" || text == "离开";
            return false;
        }

        public KeyboundFlowAction Tick(KtgMemorySnapshot snapshot, DateTime now, bool foreground)
        {
            if (!Active) return null;
            if (snapshot.ProcessId == 0) { Stop("游戏进程不可用，连续流程已停止"); return null; }
            if (processId == 0) processId = snapshot.ProcessId;
            if (snapshot.ProcessId != processId) { Stop("游戏进程已改变，连续流程已停止"); return null; }
            if (now - startedUtc > TimeSpan.FromMinutes(30)) { Stop("本局流程总超时"); return null; }
            if (now - stageUtc > TimeSpan.FromMinutes(Stage == "Combat" ? 20 : Stage == "Queue" ? 10 : 2))
            { Stop("阶段超时：" + Stage); return null; }
            ushort territory = snapshot.AfkTimer == null ? (ushort)0 : snapshot.AfkTimer.TerritoryTypeId;
            KtgAddonProbe result = FindAddon(snapshot, "KTGResult");
            if (Stage == "Npc")
            {
                if (territory != 1360) { Status = "等待位于键斗广场的报名登记员附近"; return null; }
                if (FindAddon(snapshot, "SystemMenu") != null)
                {
                    Status = "关闭已打开的系统菜单，等待界面恢复";
                    if (!foreground || now < nextActionUtc) return null;
                    if (++menuCloseAttempts > 3) { Stop("系统菜单未关闭，连续流程已停止"); return null; }
                    nextActionUtc = now.AddSeconds(2);
                    return new KeyboundFlowAction { Kind = "CloseSystemMenu", Addon = "SystemMenu" };
                }
                menuCloseAttempts = 0;
                if (FindAddon(snapshot, "KTGEnterDialogue") != null)
                    SetStage("Entry", "准备申请参加", now);
                else
                {
                    KtgObjectProbe npc = null;
                    foreach (KtgObjectProbe value in snapshot.Objects)
                        if (value.BaseId == RegistrationNpcBaseId && value.ObjectKind == 3 &&
                            value.Name == "报名登记员" && value.Targetable && IsNearby(snapshot.LocalPlayer, value))
                        { npc = value; break; }
                    if (npc == null) { Status = "等待靠近报名登记员（5 米内）"; return null; }
                    if (!foreground || now < nextActionUtc) return null;
                    if (++attempts > 4)
                    { Stop("NPC 交互未成功：请靠近报名登记员，并检查 F12（选中最近 NPC）和小键盘 0（确认）绑定"); return null; }
                    nextActionUtc = now.AddSeconds(4);
                    Status = npc.CurrentTarget ? "交互报名登记员" : "自动选择报名登记员";
                    return new KeyboundFlowAction { Kind = npc.CurrentTarget ? "Interact" : "TargetNpc" };
                }
            }
            if (Stage == "Entry" && FindAddon(snapshot, "KTGEnterDialogue") == null)
                SetStage("Queue", "等待排本确认", now);
            if (Stage == "Queue")
            {
                KtgAddonProbe confirm = FindAddon(snapshot, "ContentsFinderConfirm");
                if (confirm != null)
                {
                    bool ours = confirm.TextNodes.Exists(delegate(string text) { return text.Contains("魔光键影"); });
                    if (!ours) { Stop("排本确认不是魔光键影，已停止"); return null; }
                    return Click(confirm, now, foreground);
                }
                if (territory == 1359 && snapshot.AddonCount > 0)
                    SetStage("Combat", "已进入魔光键影，自动答题中", now);
            }
            if (Stage == "Combat")
            {
                if (result != null) SetStage("Result", "结算界面，准备退出", now);
                else if (territory == 1360) Stop("未经过结算即返回广场，连续流程已停止");
            }
            if (Stage == "Entry") return Click(FindAddon(snapshot, "KTGEnterDialogue"), now, foreground);
            if (Stage == "Result")
            {
                if (territory == 1360 && result == null)
                {
                    CompletedRuns++;
                    if (CompletedRuns >= TargetRuns)
                        Stop("连续流程完成：已返回键斗广场");
                    else
                    {
                        // Count only the acknowledged result exit. The same lobby snapshot must not
                        // queue the next run or increment the count again.
                        startedUtc = now;
                        menuCloseAttempts = 0;
                        SetStage("Npc", "准备第 " + (CompletedRuns + 1) + " 局，等待报名登记员", now);
                        nextActionUtc = now.AddSeconds(3);
                    }
                    return null;
                }
                return Click(result, now, foreground);
            }
            return null;
        }

        private KeyboundFlowAction Click(KtgAddonProbe addon, DateTime now, bool foreground)
        {
            if (addon == null || !foreground || now < nextActionUtc) return null;
            foreach (KtgButtonProbe button in addon.Buttons)
            {
                if (!button.Enabled || !IsAllowedButton(addon.Name, button.Text)) continue;
                if (++attempts > 3) { Stop("窗口操作未得到确认：" + addon.Name); return null; }
                nextActionUtc = now.AddSeconds(4);
                return new KeyboundFlowAction { Kind = "Click", Addon = addon.Name,
                    NodeId = button.NodeId, Text = button.Text };
            }
            Status = "等待可用按钮：" + addon.Name + "（按钮详情已记录）";
            return null;
        }
    }
}
