using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace ActEnhanced.KeyboundProbe
{
    public sealed class KeyboundProbePlugin : IActPluginV1
    {
        private Label pluginStatus;
        private Label connectionLabel;
        private Label captureLabel;
        private Label outputLabel;
        private Button startButton;
        private Button stopButton;
        private readonly List<Button> markerButtons = new List<Button>();
        private TextBox noteBox;
        private Timer uiTimer;
        private FfxivNetworkTap networkTap;
        private ushort actorCastOpcode;
        private ProbeCaptureSession capture;
        private string captureDirectory;
        private KeyboundLogDecoder decoder;
        private readonly KeyboundCombatLogParser combatLogParser = new KeyboundCombatLogParser();
        private KeyboundWordTable wordTable;
        private Label decoderStatusLabel;
        private Label memoryStatusLabel;
        private Label overlayStatusLabel;
        private Label scoreLabel;
        private Label phaseLabel;
        private Label priorityLabel;
        private ListView activePrompts;
        private ListView recentEvents;
        private readonly Dictionary<uint, ListViewItem> activeItems = new Dictionary<uint, ListViewItem>();
#if LEGACY_DALAMUD_BRIDGE
        private readonly HashSet<string> submittedPrompts = new HashSet<string>();
        private readonly Queue<uint> autoPromptOrder = new Queue<uint>();
        private readonly Dictionary<uint, KeyboundBridgeTarget> queuedAutoPrompts =
            new Dictionary<uint, KeyboundBridgeTarget>();
        private readonly HashSet<uint> queuedAutoMonsters = new HashSet<uint>();
        private readonly Dictionary<uint, KeyboundBridgeTarget> bridgeTargets =
            new Dictionary<uint, KeyboundBridgeTarget>();
        private KeyboundBridgeClient bridgeClient;
        private KeyboundBridgeSnapshot bridgeSnapshot;
        private Label bridgeStatusLabel;
        private DalamudAutoLauncher dalamudLauncher;
        private CheckBox autoLaunchDalamudEnabled;
        private Label dalamudLaunchStatusLabel;
#endif
        private DateTime nextConnectAttemptUtc = DateTime.MinValue;
        private CheckBox autoInputEnabled;
        private KeyboundActSettings automationSettings;
        private string automationSettingsPath;
        private CheckBox autoHumanize;
        private CheckBox autoBlatantMode;
        private NumericUpDown autoStartDelayMin;
        private NumericUpDown autoStartDelayMax;
        private NumericUpDown autoCharacterDelay;
        private NumericUpDown autoJitterPercent;
        private NumericUpDown autoSubmitDelayMin;
        private NumericUpDown autoSubmitDelayMax;
        private NumericUpDown autoTypingSubmitMinimum;
        private NumericUpDown autoTypingSubmitBudget;
        private ComboBox autoTargetOrder;
        private Label autoInputStatus;
#if LEGACY_DALAMUD_BRIDGE
        private int autoInputGeneration;
        private long autoAttemptSequence;
        private bool autoAttemptActive;
        private bool autoAwaitingConfirmation;
        private uint autoAttemptMonsterId;
        private string autoAttemptKey;
        private DateTime autoConfirmationDeadlineUtc;
        private bool autoAttemptTargetDied;
        private string autoBridgeRequestId;
#endif
        private KtgProcessMemoryReader memoryReader;
        private int memoryPollBusy;
        private readonly Dictionary<uint, KtgMemoryTarget> memoryTargets =
            new Dictionary<uint, KtgMemoryTarget>();
        private readonly DirectPromptCompletionTracker directCompletedPrompts =
            new DirectPromptCompletionTracker();
        private readonly KeyboundCombatPlanner combatPlanner = new KeyboundCombatPlanner();
        private readonly Dictionary<string, int> directFailureCounts = new Dictionary<string, int>();
        private int directInputGeneration;
        private long directInputSequence;
        private bool directAttemptActive;
        private bool directAwaitingConfirmation;
        private uint directTargetId;
        private string directTargetText;
        private uint directFocusEntityId;
        private bool directEchoVerified;
        private bool directMemoryAcknowledgementMode;
        private DateTime directConfirmationDeadlineUtc;
        private DateTime directRetryAfterUtc;
        private bool keyboundAddonPresent;
        private int activityPulseGeneration;
        private long activityPulseSequence;
        private bool activityPulseActive;
        private DateTime nextActivityPulseUtc = DateTime.MaxValue;
        private string automationDiagnosticPath;
        private readonly object automationDiagnosticLock = new object();
        private string lastMemoryDiagnosticStatus;
        private string lastBoardDiagnosticSignature;
        private KeyboundOverlayServer overlayServer;
        private string overlayDirectory;
        private string overlayMemoryStatus = "等待 FF14 进程";
        private int overlayTargetCount;
        private bool overlayInputVisible;
        private bool overlayInputFocused;
        private string overlayInputText = String.Empty;
        private string overlayInputDiagnostic = String.Empty;
        private string lastAutomationEventKind = "PLUGIN_INIT";
        private string lastAutomationEventDetail = "starting";
        private DateTime lastAutomationEventTime = DateTime.Now;
        private DateTime visualInterferenceUntilUtc = DateTime.MinValue;
        private string lastVisualObservationSignature;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            pluginStatus = pluginStatusText;
            captureDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Plugins", "KeyboundProbe", "Captures");
            string wordPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Plugins", "KeyboundProbe", "KeyboundProbe.words.tsv");
            automationSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Plugins", "KeyboundProbe", "KeyboundProbe.settings.ini");
            automationDiagnosticPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Plugins", "KeyboundProbe", "KeyboundProbe.auto.log");
            overlayDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Plugins", "KeyboundProbe", "overlay");
            actorCastOpcode = LoadActorCastOpcode();
            WriteAutomationDiagnostic("PLUGIN_INIT", 0, String.Empty, "version=" +
                Assembly.GetExecutingAssembly().GetName().Version + "; ActorCastOpcode=0x" +
                actorCastOpcode.ToString("X"));
            automationSettings = KeyboundActSettings.Load(automationSettingsPath);
            wordTable = KeyboundWordTable.Load(wordPath);
            decoder = new KeyboundLogDecoder(wordTable);

            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12)
            };
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(760, 0),
                Text = "魔光打字通：ACT 直读自动输入与机制处理（支持连续多局）\r\n题目直接读取游戏 KTGEWord 内存，不使用 Dalamud 桥接，也不使用网络题目 ID 反查词库。"
            });

            connectionLabel = AddLabel(root, "FFXIV_ACT_Plugin：正在连接…");
            memoryStatusLabel = AddLabel(root, "ACT 只读内存：等待 FF14 进程");
            overlayStatusLabel = AddLabel(root, "ngld 悬浮窗：正在启动本地状态服务…");
            decoderStatusLabel = AddLabel(root, "KTGEWord：等待游戏 UI；网络事件仅用于成功/死亡诊断。");
            scoreLabel = AddLabel(root, "分数：—");
            phaseLabel = AddLabel(root, "阶段：—");
            priorityLabel = AddLabel(root, "优先级：等待阶段事件");

            var autoControls = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            autoInputEnabled = new CheckBox
            {
                AutoSize = true,
                Text = "启用 ACT 直读自动攻击（逐只确认）",
                Checked = false,
                Enabled = true
            };
            autoInputEnabled.CheckedChanged += delegate
            {
                System.Threading.Interlocked.Increment(ref directInputGeneration);
                System.Threading.Interlocked.Increment(ref activityPulseGeneration);
                activityPulseActive = false;
                ResetDirectAutomation(!autoInputEnabled.Checked);
                nextActivityPulseUtc = autoInputEnabled.Checked
                    ? DateTime.UtcNow.AddMilliseconds(NextActivityPulseDelay(true))
                    : DateTime.MaxValue;
                autoInputStatus.Text = autoInputEnabled.Checked
                    ? "自动输入：已启用；请保持 FF14 在前台"
                    : "自动输入：已停用";
                WriteAutomationDiagnostic(autoInputEnabled.Checked ? "AUTOMATION_ON" : "AUTOMATION_OFF",
                    0, String.Empty, GameInputSender.DescribeFfxivInputTarget());
            };
            autoControls.Controls.Add(autoInputEnabled);
            AddButton(autoControls, "立即停用", delegate
            {
                autoInputEnabled.Checked = false;
                autoInputStatus.Text = "自动输入：已紧急停用";
            });
            root.Controls.Add(autoControls);

            var rhythmControls = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new System.Drawing.Size(780, 0) };
            autoHumanize = new CheckBox { AutoSize = true, Text = "拟人化", Checked = automationSettings.Humanize };
            autoBlatantMode = new CheckBox { AutoSize = true, Text = "演都不演", Checked = automationSettings.BlatantMode };
            rhythmControls.Controls.Add(autoHumanize);
            rhythmControls.Controls.Add(autoBlatantMode);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "反应" });
            autoStartDelayMin = AddNumeric(rhythmControls, 0, 5000, automationSettings.StartDelayMinMs, 70);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "–" });
            autoStartDelayMax = AddNumeric(rhythmControls, 0, 5000, automationSettings.StartDelayMaxMs, 70);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "ms；每字" });
            autoCharacterDelay = AddNumeric(rhythmControls, 0, 500, automationSettings.CharacterDelayMs, 60);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "ms；抖动" });
            autoJitterPercent = AddNumeric(rhythmControls, 0, 80, automationSettings.JitterPercent, 55);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "%；提交" });
            autoSubmitDelayMin = AddNumeric(rhythmControls, 0, 2000, automationSettings.SubmitDelayMinMs, 65);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "–" });
            autoSubmitDelayMax = AddNumeric(rhythmControls, 0, 2000, automationSettings.SubmitDelayMaxMs, 65);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "ms；输入→回车最短" });
            autoTypingSubmitMinimum = AddNumeric(rhythmControls, 0, 10000,
                automationSettings.TypingSubmitMinimumMs, 70);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "ms；最长预算" });
            autoTypingSubmitBudget = AddNumeric(rhythmControls, 250, 10000,
                automationSettings.TypingSubmitBudgetMs, 70);
            rhythmControls.Controls.Add(new Label { AutoSize = true, Text = "ms" });
            root.Controls.Add(rhythmControls);

            var orderControls = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            orderControls.Controls.Add(new Label { AutoSize = true, Text = "目标顺序" });
            autoTargetOrder = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            autoTargetOrder.Items.AddRange(new object[] { "槽位近→远（近似距离）", "槽位远→近", "短词优先", "长词优先" });
            autoTargetOrder.SelectedIndex = TargetOrderIndex(automationSettings.TargetOrder);
            orderControls.Controls.Add(autoTargetOrder);
            root.Controls.Add(orderControls);

            EventHandler saveRhythm = delegate { ReadAndSaveAutomationSettings(); };
            autoHumanize.CheckedChanged += saveRhythm;
            autoBlatantMode.CheckedChanged += saveRhythm;
            autoStartDelayMin.ValueChanged += saveRhythm;
            autoStartDelayMax.ValueChanged += saveRhythm;
            autoCharacterDelay.ValueChanged += saveRhythm;
            autoJitterPercent.ValueChanged += saveRhythm;
            autoSubmitDelayMin.ValueChanged += saveRhythm;
            autoSubmitDelayMax.ValueChanged += saveRhythm;
            autoTypingSubmitMinimum.ValueChanged += saveRhythm;
            autoTypingSubmitBudget.ValueChanged += saveRhythm;
            autoTargetOrder.SelectedIndexChanged += saveRhythm;
            SaveAutomationSettings();
            autoInputStatus = AddLabel(root, "自动输入：已停用");
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(760, 0),
                Text = "题目来自 ACT 直读 KTGEWord；通过 FF14 窗口消息逐字输入并逐字校验 KTGInput 回显，一次锁定一只怪物。输入到回车的最短时间和最长预算可在上方调整；输入框清空或同实体题目切换后继续，ACT 成功/死亡事件用于延迟佐证。KTGInput 不可读时禁止盲打。"
            });

            root.Controls.Add(new Label { AutoSize = true, Text = "当前题目" });
            activePrompts = CreateEventList(760, 170);
            activePrompts.Columns.Add("怪物", 95);
            activePrompts.Columns.Add("槽位", 65);
            activePrompts.Columns.Add("显示文字", 220);
            activePrompts.Columns.Add("应输入", 280);
            activePrompts.Columns.Add("状态", 80);
            root.Controls.Add(activePrompts);

            root.Controls.Add(new Label { AutoSize = true, Text = "最近事件" });
            recentEvents = CreateEventList(760, 190);
            recentEvents.Columns.Add("时间", 80);
            recentEvents.Columns.Add("事件", 80);
            recentEvents.Columns.Add("怪物", 95);
            recentEvents.Columns.Add("题目ID", 65);
            recentEvents.Columns.Add("显示文字", 185);
            recentEvents.Columns.Add("应输入/详情", 245);
            root.Controls.Add(recentEvents);

            captureLabel = AddLabel(root, "尚未开始捕获。");
            outputLabel = AddLabel(root, "输出目录：" + captureDirectory);

            var controls = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            startButton = AddButton(controls, "开始捕获", StartCapture);
            stopButton = AddButton(controls, "停止并生成摘要", StopCapture);
            root.Controls.Add(controls);

            var markers = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            AddMarker(markers, "标记：进入玩法", "ENTER_DUTY");
            AddMarker(markers, "标记：题目出现", "PROMPT_APPEARED");
            AddMarker(markers, "标记：输入成功", "INPUT_SUCCESS");
            AddMarker(markers, "标记：怪物死亡", "MONSTER_DIED");
            root.Controls.Add(markers);

            var noteRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            noteBox = new TextBox { Width = 360 };
            noteRow.Controls.Add(noteBox);
            AddMarker(noteRow, "添加自定义标记", null);
            root.Controls.Add(noteRow);

            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(760, 0),
                Text = "建议流程：在进入玩法前开始捕获 → 连续打几局 → 结束后停止捕获。若方便，每种事件点几次标记会更容易定位；不点也能按重复时序和 opcode 差异离线筛选。不要无人值守跨天捕获，原始包可能包含角色和区域相关数据。"
            });
            pluginScreenSpace.Controls.Add(root);

            ActGlobals.oFormActMain.OnLogLineRead += OnActLogLineRead;
            networkTap = new FfxivNetworkTap(OnNetworkReceived);
            networkTap.Connect();
            memoryReader = new KtgProcessMemoryReader();
            overlayServer = new KeyboundOverlayServer(overlayDirectory);
            overlayServer.AutomationRequested += OnOverlayAutomationRequested;
            overlayStatusLabel.Text = String.IsNullOrEmpty(overlayServer.Error)
                ? "ngld 悬浮窗：本地状态服务 127.0.0.1:" + overlayServer.Port
                : "ngld 悬浮窗：启动失败；" + overlayServer.Error;
            uiTimer = new Timer { Interval = 500 };
            uiTimer.Tick += delegate { RefreshUi(); };
            uiTimer.Start();
            RefreshUi();
            pluginStatus.Text = "魔光打字通已加载";
        }

        public void DeInitPlugin()
        {
            WriteAutomationDiagnostic("PLUGIN_DEINIT", 0, String.Empty, "stopping");
            if (autoInputEnabled != null)
                autoInputEnabled.Checked = false;
            ActGlobals.oFormActMain.OnLogLineRead -= OnActLogLineRead;
            if (uiTimer != null)
            {
                uiTimer.Stop();
                uiTimer.Dispose();
                uiTimer = null;
            }
            if (memoryReader != null)
            {
                memoryReader.Dispose();
                memoryReader = null;
            }
            if (overlayServer != null)
            {
                overlayServer.AutomationRequested -= OnOverlayAutomationRequested;
                overlayServer.Dispose();
                overlayServer = null;
            }
            StopCaptureInternal(false);
            if (networkTap != null)
            {
                networkTap.Dispose();
                networkTap = null;
            }
            if (pluginStatus != null)
                pluginStatus.Text = "魔光打字通已停用";
        }

        private static Label AddLabel(Control parent, string text)
        {
            var label = new Label { AutoSize = true, Text = text };
            parent.Controls.Add(label);
            return label;
        }

        private static NumericUpDown AddNumeric(Control parent, int minimum, int maximum, int value, int width)
        {
            var control = new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Min(maximum, Math.Max(minimum, value)),
                Width = width
            };
            parent.Controls.Add(control);
            return control;
        }

        private void ReadAndSaveAutomationSettings()
        {
            if (automationSettings == null || autoHumanize == null || autoTargetOrder == null)
                return;
            automationSettings.Humanize = autoHumanize.Checked;
            automationSettings.BlatantMode = autoBlatantMode.Checked;
            automationSettings.StartDelayMinMs = Decimal.ToInt32(autoStartDelayMin.Value);
            automationSettings.StartDelayMaxMs = Decimal.ToInt32(autoStartDelayMax.Value);
            automationSettings.CharacterDelayMs = Decimal.ToInt32(autoCharacterDelay.Value);
            automationSettings.JitterPercent = Decimal.ToInt32(autoJitterPercent.Value);
            automationSettings.SubmitDelayMinMs = Decimal.ToInt32(autoSubmitDelayMin.Value);
            automationSettings.SubmitDelayMaxMs = Decimal.ToInt32(autoSubmitDelayMax.Value);
            automationSettings.TypingSubmitMinimumMs = Decimal.ToInt32(autoTypingSubmitMinimum.Value);
            automationSettings.TypingSubmitBudgetMs = Decimal.ToInt32(autoTypingSubmitBudget.Value);
            automationSettings.TargetOrder = TargetOrderName(autoTargetOrder.SelectedIndex);
            automationSettings.Normalize();
            SaveAutomationSettings();
            if (autoTypingSubmitMinimum.Value != automationSettings.TypingSubmitMinimumMs)
                autoTypingSubmitMinimum.Value = automationSettings.TypingSubmitMinimumMs;
            if (autoTypingSubmitBudget.Value != automationSettings.TypingSubmitBudgetMs)
                autoTypingSubmitBudget.Value = automationSettings.TypingSubmitBudgetMs;
        }

        private void SaveAutomationSettings()
        {
            try
            {
                if (automationSettings != null && !String.IsNullOrEmpty(automationSettingsPath))
                    automationSettings.Save(automationSettingsPath);
            }
            catch (Exception ex)
            {
                if (autoInputStatus != null)
                    autoInputStatus.Text = "设置保存失败：" + ex.Message;
            }
        }

        private static int TargetOrderIndex(string order)
        {
            if (order == "slot_desc") return 1;
            if (order == "shortest") return 2;
            if (order == "longest") return 3;
            return 0;
        }

        private static string TargetOrderName(int index)
        {
            if (index == 1) return "slot_desc";
            if (index == 2) return "shortest";
            if (index == 3) return "longest";
            return "slot_asc";
        }

        private static Button AddButton(Control parent, string text, EventHandler click)
        {
            var button = new Button { AutoSize = true, Text = text };
            button.Click += click;
            parent.Controls.Add(button);
            return button;
        }

        private static ListView CreateEventList(int width, int height)
        {
            return new ListView
            {
                Width = width,
                Height = height,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false
            };
        }

        private void AddMarker(Control parent, string text, string marker)
        {
            Button button = AddButton(parent, text, delegate
            {
                string label = marker;
                if (label == null)
                {
                    label = noteBox == null ? null : noteBox.Text;
                    if (noteBox != null)
                        noteBox.Clear();
                }
                if (capture != null && capture.IsAccepting && !String.IsNullOrWhiteSpace(label))
                    capture.Mark(label);
            });
            markerButtons.Add(button);
        }

        private void StartCapture(object sender, EventArgs args)
        {
            if (capture != null && capture.IsAccepting)
                return;
            if (!networkTap.IsConnected && !networkTap.Connect())
            {
                MessageBox.Show("无法连接 FFXIV_ACT_Plugin：" + networkTap.Error,
                    "魔光打字通", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                capture = new ProbeCaptureSession(captureDirectory);
                capture.Mark("CAPTURE_STARTED");
                outputLabel.Text = "当前捕获：" + capture.BinaryPath;
                RefreshUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show("开始捕获失败：" + ex.Message,
                    "魔光打字通", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopCapture(object sender, EventArgs args)
        {
            StopCaptureInternal(true);
        }

        private void StopCaptureInternal(bool showResult)
        {
            ProbeCaptureSession current = capture;
            capture = null;
            if (current == null)
                return;

            string summary = current.SummaryPath;
            try
            {
                current.Mark("CAPTURE_STOP_REQUESTED");
                current.Stop();
                if (showResult)
                    MessageBox.Show("捕获已完成。\r\n\r\n摘要：" + summary,
                        "魔光打字通", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (showResult)
                    MessageBox.Show("停止捕获时发生错误：" + ex.Message,
                        "魔光打字通", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                current.Dispose();
                RefreshUi();
            }
        }

        private void OnNetworkReceived(string connection, long epoch, byte[] message)
        {
            uint actorId;
            uint actionId;
            double castSeconds;
            if (PacketMetadata.TryReadActorCast(message, actorCastOpcode,
                out actorId, out actionId, out castSeconds) &&
                KeyboundCombatPlanner.IsStopCastAction(actionId))
            {
                combatPlanner.RecordCastStart(actorId, actionId, castSeconds, DateTime.UtcNow);
                WriteAutomationDiagnostic("CAST_FAST", actorId, String.Empty,
                    "action=0x" + actionId.ToString("X") + "; castSeconds=" +
                    castSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                    "; opcode=0x" + actorCastOpcode.ToString("X"));
            }
            ProbeCaptureSession current = capture;
            if (current != null && current.IsAccepting)
                current.Record(connection, epoch, message);
        }

        private static ushort LoadActorCastOpcode()
        {
            string dataRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Plugins", "Data", "FFXIV_ACT_Plugin");
            ushort value = PacketMetadata.LoadOpcode(
                Path.Combine(dataRoot, "Chinese", "OpCode.txt"), "ActorCast");
            return value != 0 ? value : PacketMetadata.LoadOpcode(
                Path.Combine(dataRoot, "Global", "OpCode.txt"), "ActorCast");
        }

        private void OnActLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            if (isImport || logInfo == null)
                return;
            string line = String.IsNullOrEmpty(logInfo.originalLogLine)
                ? logInfo.logLine
                : logInfo.originalLogLine;
            KeyboundEvent decoded;
            KeyboundCombatSignal combatSignal;
            bool hasDecoded = decoder.TryDecode(0, line, out decoded);
            bool hasCombatSignal = combatLogParser.TryParse(line, out combatSignal);
            if (!hasDecoded && !hasCombatSignal)
                return;
            if (activePrompts == null || activePrompts.IsDisposed)
                return;
            activePrompts.BeginInvoke((Action)(delegate
            {
                if (hasCombatSignal)
                    ApplyCombatSignal(combatSignal);
                if (hasDecoded)
                    ApplyDecodedEvent(decoded);
            }));
        }

        private void ScheduleMemoryPoll()
        {
            if (memoryReader == null || activePrompts == null || activePrompts.IsDisposed ||
                System.Threading.Interlocked.CompareExchange(ref memoryPollBusy, 1, 0) != 0)
                return;
            KtgProcessMemoryReader reader = memoryReader;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                KtgMemorySnapshot snapshot = reader.ReadSnapshot();
                if (activePrompts == null || activePrompts.IsDisposed)
                {
                    System.Threading.Interlocked.Exchange(ref memoryPollBusy, 0);
                    return;
                }
                try
                {
                    activePrompts.BeginInvoke((Action)(delegate
                    {
                        try { ApplyMemorySnapshot(snapshot); }
                        finally { System.Threading.Interlocked.Exchange(ref memoryPollBusy, 0); }
                    }));
                }
                catch
                {
                    System.Threading.Interlocked.Exchange(ref memoryPollBusy, 0);
                }
            });
        }

        private void ApplyMemorySnapshot(KtgMemorySnapshot snapshot)
        {
            keyboundAddonPresent = snapshot.AddonCount > 0;
            overlayMemoryStatus = snapshot.Status ?? String.Empty;
            overlayTargetCount = snapshot.Targets.Count;
            overlayInputVisible = snapshot.InputVisible;
            overlayInputFocused = snapshot.InputFocused;
            overlayInputText = snapshot.InputText ?? String.Empty;
            overlayInputDiagnostic = snapshot.InputDiagnostic ?? String.Empty;
            memoryStatusLabel.Text = "ACT 只读内存：" +
                (String.IsNullOrEmpty(snapshot.Status) ? "等待数据" : snapshot.Status);
            snapshot.Targets.Sort(delegate(KtgMemoryTarget left, KtgMemoryTarget right)
            {
                int addon = left.AddonIndex.CompareTo(right.AddonIndex);
                return addon != 0 ? addon : left.Slot.CompareTo(right.Slot);
            });
            foreach (KtgMemoryTarget observedTarget in snapshot.Targets)
                combatPlanner.ObserveTarget(observedTarget.EntityId);
            if (DateTime.UtcNow <= visualInterferenceUntilUtc)
            {
                string visualSignature = snapshot.Targets.Count + ":" + snapshot.AddonCount + ":" +
                    snapshot.InputVisible + ":" + snapshot.InputFocused + ":" + snapshot.InputText;
                if (!String.Equals(lastVisualObservationSignature, visualSignature, StringComparison.Ordinal))
                {
                    lastVisualObservationSignature = visualSignature;
                    WriteAutomationDiagnostic("VISION_OBSERVE", 0, String.Empty,
                        "targets=" + snapshot.Targets.Count + "; addons=" + snapshot.AddonCount +
                        "; inputVisible=" + snapshot.InputVisible + "; inputFocused=" + snapshot.InputFocused +
                        "; inputLength=" + snapshot.InputText.Length);
                }
            }
            if (directAttemptActive && directAwaitingConfirmation && directEchoVerified &&
                snapshot.InputDiagnostic.StartsWith("ok;", StringComparison.Ordinal) &&
                snapshot.InputText.Length == 0)
                ConfirmDirectAcceptance(directTargetId, directTargetText,
                    "KTGInput 已清空", "MEMORY_INPUT_CLEAR");
            if (!String.Equals(lastMemoryDiagnosticStatus, snapshot.Status, StringComparison.Ordinal))
            {
                lastMemoryDiagnosticStatus = snapshot.Status;
                WriteAutomationDiagnostic("MEMORY", 0, String.Empty, snapshot.Status);
            }
            var boardDiagnostic = new System.Text.StringBuilder();
            foreach (KtgMemoryTarget boardTarget in snapshot.Targets)
                boardDiagnostic.Append(boardTarget.EntityId.ToString("X8")).Append(':')
                    .Append(boardTarget.Slot).Append(':').Append(boardTarget.Text).Append('|');
            string boardSignature = boardDiagnostic.ToString();
            if (!String.Equals(lastBoardDiagnosticSignature, boardSignature, StringComparison.Ordinal))
            {
                lastBoardDiagnosticSignature = boardSignature;
                WriteAutomationDiagnostic("BOARD", 0, String.Empty,
                    boardSignature.Length == 0 ? "empty" : boardSignature);
            }
            var visible = new HashSet<uint>();
            foreach (KtgMemoryTarget target in snapshot.Targets)
            {
                visible.Add(target.EntityId);
                KtgMemoryTarget previous;
                bool changed = !memoryTargets.TryGetValue(target.EntityId, out previous) ||
                    !String.Equals(previous.Text, target.Text, StringComparison.Ordinal);
                if (directAttemptActive && directAwaitingConfirmation &&
                    target.EntityId == directTargetId &&
                    !String.Equals(target.Text, directTargetText, StringComparison.Ordinal))
                    ConfirmDirectAcceptance(directTargetId, directTargetText,
                        "同一怪物题目已切换为 “" + target.Text + "”", "MEMORY_PROMPT_CHANGED");
                if (directCompletedPrompts.Observe(target.EntityId, target.Text) && previous != null)
                    directFailureCounts.Remove(DirectTargetKey(target.EntityId, previous.Text));
                memoryTargets[target.EntityId] = target;

                ListViewItem item;
                if (!activeItems.TryGetValue(target.EntityId, out item))
                {
                    item = new ListViewItem();
                    item.SubItems.Add(String.Empty);
                    item.SubItems.Add(String.Empty);
                    item.SubItems.Add(String.Empty);
                    item.SubItems.Add(String.Empty);
                    activeItems[target.EntityId] = item;
                    activePrompts.Items.Add(item);
                }
                item.Text = "0x" + target.EntityId.ToString("X8");
                item.SubItems[1].Text = "A" + target.AddonIndex + "/槽位 " + target.Slot;
                item.SubItems[2].Text = target.Text;
                item.SubItems[3].Text = target.Text;
                if (directAttemptActive && directTargetId == target.EntityId)
                    item.SubItems[4].Text = directAwaitingConfirmation ? "等待确认" : "输入中";
                else
                    item.SubItems[4].Text = target.Obstructed
                        ? "遮挡 S=" + target.State
                        : "可见 S=" + target.State;
                if (changed)
                    AddMemoryRecent(target);
            }

            var removed = new List<uint>();
            foreach (uint entityId in memoryTargets.Keys)
                if (!visible.Contains(entityId))
                    removed.Add(entityId);
            foreach (uint entityId in removed)
            {
                if (directAttemptActive && directTargetId == entityId)
                {
                    WriteAutomationDiagnostic("TARGET_GONE", entityId, directTargetText,
                        directAwaitingConfirmation ? "while-waiting-confirmation" : "while-sending");
                    if (directAwaitingConfirmation && directEchoVerified)
                        ConfirmDirectAcceptance(entityId, directTargetText,
                            "题目实体已消失", "MEMORY_TARGET_GONE");
                }
                if (directFocusEntityId == entityId)
                    directFocusEntityId = 0;
                directCompletedPrompts.RemoveEntity(entityId);
                RemoveDirectFailuresForEntity(entityId);
                memoryTargets.Remove(entityId);
                ListViewItem item;
                if (activeItems.TryGetValue(entityId, out item))
                {
                    activeItems.Remove(entityId);
                    activePrompts.Items.Remove(item);
                }
            }
            decoderStatusLabel.Text = "KTGEWord 直读：" + snapshot.Targets.Count +
                " 个可见中文题目；最近刷新 " + DateTime.Now.ToString("HH:mm:ss") + "。";
            TryStartDirectInput(snapshot.Targets);
        }

        private void AddMemoryRecent(KtgMemoryTarget target)
        {
            var item = new ListViewItem(DateTime.Now.ToString("HH:mm:ss.fff"));
            item.SubItems.Add("内存题目");
            item.SubItems.Add("0x" + target.EntityId.ToString("X8"));
            item.SubItems.Add(String.Empty);
            item.SubItems.Add(target.Text);
            item.SubItems.Add("KTGEWord A" + target.AddonIndex + "/槽位 " + target.Slot +
                "；State=" + target.State + (target.Obstructed ? "；遮挡" : String.Empty));
            recentEvents.Items.Insert(0, item);
            while (recentEvents.Items.Count > 200)
                recentEvents.Items.RemoveAt(recentEvents.Items.Count - 1);
        }

        private void TryStartDirectInput(IList<KtgMemoryTarget> available)
        {
            if (autoInputEnabled == null || !autoInputEnabled.Checked || directAttemptActive ||
                DateTime.UtcNow < directRetryAfterUtc || available == null)
                return;
            if (!combatPlanner.CanAttack)
            {
                autoInputStatus.Text = "自动输入：等待战斗阶段 0x2";
                return;
            }
            if (combatPlanner.ShouldPause(DateTime.UtcNow))
            {
                autoInputStatus.Text = "自动输入：" + combatPlanner.PauseDescription(DateTime.UtcNow);
                return;
            }
            TryStartActivityPulse();
            if (activityPulseActive)
                return;
            bool visualInterferenceActive = DateTime.UtcNow <= visualInterferenceUntilUtc;
            var candidates = new List<KtgMemoryTarget>();
            foreach (KtgMemoryTarget target in available)
            {
                if (!KeyboundCombatPlanner.CanUsePrompt(target.Obstructed,
                        visualInterferenceActive) || String.IsNullOrWhiteSpace(target.Text) ||
                    GameInputSender.NormalizeChineseAnswer(target.Text).Length == 0 ||
                    directCompletedPrompts.IsSuppressed(target.EntityId, target.Text))
                    continue;
                candidates.Add(target);
            }
            if (candidates.Count == 0)
            {
                autoInputStatus.Text = visualInterferenceActive
                    ? "自动输入：视觉妨碍中，等待可读取题目"
                    : "自动输入：等待可提交的中文题目";
                return;
            }
            DateTime selectionUtc = DateTime.UtcNow;
            candidates.Sort(delegate(KtgMemoryTarget left, KtgMemoryTarget right)
            {
                int planned = combatPlanner.CompareTargets(
                    left.EntityId, left.Slot, right.EntityId, right.Slot,
                    directFocusEntityId, selectionUtc);
                if (planned != 0)
                    return planned;
                string order = automationSettings == null ? "slot_asc" : automationSettings.TargetOrder;
                if (order == "slot_desc") return right.Slot.CompareTo(left.Slot);
                if (order == "shortest")
                {
                    int length = left.Text.Length.CompareTo(right.Text.Length);
                    return length != 0 ? length : left.Slot.CompareTo(right.Slot);
                }
                if (order == "longest")
                {
                    int length = right.Text.Length.CompareTo(left.Text.Length);
                    return length != 0 ? length : left.Slot.CompareTo(right.Slot);
                }
                return left.Slot.CompareTo(right.Slot);
            });
            StartDirectInput(candidates[0]);
        }

        private void StartDirectInput(KtgMemoryTarget target)
        {
            directFocusEntityId = target.EntityId;
            long sequence = System.Threading.Interlocked.Increment(ref directInputSequence);
            int generation = System.Threading.Interlocked.CompareExchange(ref directInputGeneration, 0, 0);
            directAttemptActive = true;
            directAwaitingConfirmation = false;
            directTargetId = target.EntityId;
            directTargetText = target.Text;
            directEchoVerified = false;
            directConfirmationDeadlineUtc = DateTime.MinValue;

            var random = new Random(unchecked(Environment.TickCount * 31 + (int)(sequence & 0x7FFFFFFF)));
            bool blatant = automationSettings != null && automationSettings.BlatantMode;
            bool humanize = automationSettings == null || automationSettings.Humanize;
            int startDelay = blatant ? 0 : RandomDelay(random,
                automationSettings.StartDelayMinMs,
                humanize ? automationSettings.StartDelayMaxMs : automationSettings.StartDelayMinMs);
            int characterDelay = blatant ? 0 : automationSettings.CharacterDelayMs;
            if (!blatant && humanize && automationSettings.JitterPercent != 0)
            {
                int jitter = characterDelay * automationSettings.JitterPercent / 100;
                characterDelay = RandomDelay(random, Math.Max(0, characterDelay - jitter),
                    characterDelay + jitter);
            }
            int submitDelay = blatant ? 0 : RandomDelay(random,
                automationSettings.SubmitDelayMinMs,
                humanize ? automationSettings.SubmitDelayMaxMs : automationSettings.SubmitDelayMinMs);
            automationSettings.Normalize();
            int typingSubmitMinimumMs = automationSettings.TypingSubmitMinimumMs;
            int typingSubmitBudgetMs = automationSettings.TypingSubmitBudgetMs;
            string typedText = GameInputSender.NormalizeChineseAnswer(target.Text);
            autoInputStatus.Text = "自动输入：窗口投递 “" + target.Text + "” → 0x" +
                target.EntityId.ToString("X8");
            WriteAutomationDiagnostic("SELECT", target.EntityId, target.Text, "slot=" + target.Slot +
                "; directorPhase=0x" + combatPlanner.DirectorPhase.ToString("X") +
                "; combatStage=P" + combatPlanner.CombatStage +
                "; bnpc=0x" + combatPlanner.GetActorType(target.EntityId).ToString("X") +
                "; appearance=" + combatPlanner.GetAppearanceOrder(target.EntityId) +
                "; " + GameInputSender.DescribeFfxivInputTarget());

            var worker = new System.Threading.Thread((System.Threading.ThreadStart)delegate
            {
                bool echoVerified = false;
                bool inputCleared = false;
                Func<bool> current = delegate
                {
                    return System.Threading.Interlocked.CompareExchange(ref directInputGeneration, 0, 0) == generation &&
                        System.Threading.Interlocked.Read(ref directInputSequence) == sequence &&
                        combatPlanner.CanAttack && !combatPlanner.ShouldPause(DateTime.UtcNow);
                };
                if (startDelay != 0) System.Threading.Thread.Sleep(startDelay);
                GameInputResult result = EnsureKeyboundInputFocused(target, current);
                WriteAutomationDiagnostic(result == GameInputResult.Sent ? "FOCUS_OK" : "FOCUS_FAIL",
                    target.EntityId, target.Text, result + "; " + GameInputSender.DescribeFfxivInputTarget());
                if (result == GameInputResult.Sent)
                {
                    DateTime typingStartedUtc = DateTime.UtcNow;
                    DateTime earliestSubmitUtc = typingStartedUtc.AddMilliseconds(
                        typingSubmitMinimumMs);
                    DateTime submitDeadlineUtc = typingStartedUtc.AddMilliseconds(
                        typingSubmitBudgetMs);
                    result = TypeTextWithEcho(target, typedText, characterDelay, current,
                        submitDeadlineUtc);
                    echoVerified = result == GameInputResult.Sent;
                    WriteAutomationDiagnostic(result == GameInputResult.Sent ? "TEXT_OK" : "TEXT_FAIL",
                        target.EntityId, target.Text, result + "; typed=" + typedText +
                        "; minimumMs=" + typingSubmitMinimumMs +
                        "; budgetMs=" + typingSubmitBudgetMs);
                    if (result == GameInputResult.Sent)
                    {
                        int remaining = RemainingMilliseconds(submitDeadlineUtc);
                        int boundedSubmitDelay = Math.Min(submitDelay, Math.Max(0, remaining - 10));
                        DateTime configuredSubmitUtc = DateTime.UtcNow.AddMilliseconds(boundedSubmitDelay);
                        DateTime notBeforeUtc = configuredSubmitUtc > earliestSubmitUtc
                            ? configuredSubmitUtc : earliestSubmitUtc;
                        result = WaitUntilSubmitWindow(notBeforeUtc, submitDeadlineUtc, current)
                            ? GameInputSender.PostEnterToFfxiv(current)
                            : (current() ? GameInputResult.NativeInputFailed : GameInputResult.Cancelled);
                        if (result == GameInputResult.Sent)
                            inputCleared = WaitForInputText(String.Empty,
                                Math.Min(350, RemainingMilliseconds(submitDeadlineUtc)), current);
                    }
                    WriteAutomationDiagnostic(result == GameInputResult.Sent ? "ENTER_OK" : "ENTER_FAIL",
                        target.EntityId, target.Text, result + "; remainingMs=" +
                        RemainingMilliseconds(submitDeadlineUtc) + "; elapsedMs=" +
                        Math.Max(0, (int)(DateTime.UtcNow - typingStartedUtc).TotalMilliseconds) +
                        "; inputCleared=" + inputCleared);
                }
                if (autoInputStatus == null || autoInputStatus.IsDisposed)
                    return;
                try
                {
                    autoInputStatus.BeginInvoke((Action)(delegate
                    {
                        ApplyDirectInputResult(sequence, target, result, echoVerified, inputCleared);
                    }));
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
        }

        private static bool WaitUntilSubmitWindow(DateTime notBeforeUtc, DateTime deadlineUtc,
            Func<bool> current)
        {
            while (DateTime.UtcNow < notBeforeUtc)
            {
                if (!current() || DateTime.UtcNow >= deadlineUtc)
                    return false;
                int remaining = Math.Max(1, (int)(notBeforeUtc - DateTime.UtcNow).TotalMilliseconds);
                System.Threading.Thread.Sleep(Math.Min(25, remaining));
            }
            return current() && DateTime.UtcNow < deadlineUtc;
        }

        private void ApplyDirectInputResult(long sequence, KtgMemoryTarget target, GameInputResult result,
            bool echoVerified, bool inputCleared)
        {
            if (System.Threading.Interlocked.Read(ref directInputSequence) != sequence)
                return;
            if (result == GameInputResult.Sent)
            {
                directEchoVerified = echoVerified;
                if (inputCleared)
                {
                    ConfirmDirectAcceptance(target.EntityId, target.Text,
                        "KTGInput 提交后已清空", "INPUT_CLEAR_AFTER_ENTER");
                    return;
                }
                directAwaitingConfirmation = true;
                directConfirmationDeadlineUtc = DateTime.UtcNow.AddSeconds(10);
                autoInputStatus.Text = "自动输入：已回车提交 “" + target.Text +
                    "”，等待输入框清空或题目切换";
                WriteAutomationDiagnostic("SUBMIT", target.EntityId, target.Text,
                    "waiting-memory-confirmation; echoVerified=" + echoVerified);
                return;
            }
            directAttemptActive = false;
            directAwaitingConfirmation = false;
            directTargetId = 0;
            directTargetText = null;
            directEchoVerified = false;
            directRetryAfterUtc = DateTime.UtcNow.AddSeconds(1);
            autoInputStatus.Text = combatPlanner.ShouldPause(DateTime.UtcNow)
                ? "自动输入：" + combatPlanner.PauseDescription(DateTime.UtcNow)
                : "自动输入：窗口投递失败（" + result + "），稍后重试";
            WriteAutomationDiagnostic("SEND_FAIL", target.EntityId, target.Text, result.ToString());
        }

        private void ConfirmDirectAttack(uint monsterId, string confirmation)
        {
            WriteAutomationDiagnostic("NETWORK_EVENT", monsterId,
                directAttemptActive && directTargetId == monsterId ? directTargetText : String.Empty,
                confirmation + "; active=0x" + directTargetId.ToString("X8") +
                "; awaiting=" + directAwaitingConfirmation);
            bool targetDied = String.Equals(confirmation, "目标死亡", StringComparison.Ordinal);
            if (targetDied && directFocusEntityId == monsterId)
                directFocusEntityId = 0;
            if (!directAttemptActive || directTargetId != monsterId)
                return;
            if (directMemoryAcknowledgementMode && !targetDied)
            {
                WriteAutomationDiagnostic("NETWORK_CORROBORATE", monsterId, directTargetText,
                    confirmation + "; ignored-for-current-prompt");
                return;
            }
            ConfirmDirectAcceptance(monsterId, directTargetText, confirmation,
                targetDied ? "NETWORK_DEATH" : "NETWORK_SUCCESS");
        }

        private void ConfirmDirectAcceptance(uint monsterId, string submittedText,
            string confirmation, string source)
        {
            if (!directAttemptActive || directTargetId != monsterId ||
                !String.Equals(directTargetText, submittedText, StringComparison.Ordinal))
                return;
            string completedKey = DirectTargetKey(directTargetId, directTargetText);
            directCompletedPrompts.MarkCompleted(directTargetId, directTargetText);
            directFailureCounts.Remove(completedKey);
            string completedText = directTargetText;
            if (source.StartsWith("MEMORY_", StringComparison.Ordinal) ||
                source == "INPUT_CLEAR_AFTER_ENTER")
                directMemoryAcknowledgementMode = true;
            ResetDirectAutomation(false);
            directRetryAfterUtc = DateTime.UtcNow.AddMilliseconds(80);
            autoInputStatus.Text = "自动输入：" + confirmation + " “" + completedText + "”；继续当前怪物";
            WriteAutomationDiagnostic("CONFIRM", monsterId, completedText,
                confirmation + "; source=" + source);
        }

        private void ResetDirectAutomation(bool clearCompleted)
        {
            System.Threading.Interlocked.Increment(ref directInputSequence);
            directAttemptActive = false;
            directAwaitingConfirmation = false;
            directTargetId = 0;
            directTargetText = null;
            directEchoVerified = false;
            directConfirmationDeadlineUtc = DateTime.MinValue;
            directRetryAfterUtc = DateTime.MinValue;
            if (clearCompleted)
            {
                directCompletedPrompts.Clear();
                directFailureCounts.Clear();
                directFocusEntityId = 0;
                directMemoryAcknowledgementMode = false;
            }
        }

        private GameInputResult TypeTextWithEcho(KtgMemoryTarget target, string typedText,
            int characterDelay, Func<bool> current, DateTime submitDeadlineUtc)
        {
            KtgInputState initial = memoryReader == null ? null : memoryReader.ReadInputState();
            if (initial == null || !initial.Available)
            {
                WriteAutomationDiagnostic("ECHO_UNAVAILABLE", target.EntityId, target.Text,
                    "submission-blocked; no blind fallback");
                return GameInputResult.NativeInputFailed;
            }
            if (!ClearInputWithEcho(target, initial.Text, current, submitDeadlineUtc))
                return GameInputResult.NativeInputFailed;

            string expected = String.Empty;
            for (int characterIndex = 0; characterIndex < typedText.Length; characterIndex++)
            {
                char character = typedText[characterIndex];
                if (!current()) return GameInputResult.Cancelled;
                if (RemainingMilliseconds(submitDeadlineUtc) <= 0)
                    return GameInputResult.NativeInputFailed;
                expected += character;
                GameInputResult result = GameInputResult.NativeInputFailed;
                bool echoed = false;
                for (int attempt = 1; attempt <= 3 && !echoed; attempt++)
                {
                    result = GameInputSender.PostUnicodeToFfxiv(character.ToString(), 0, current);
                    if (result != GameInputResult.Sent)
                        break;
                    echoed = WaitForInputText(expected,
                        Math.Min(300, RemainingMilliseconds(submitDeadlineUtc)), current);
                    WriteAutomationDiagnostic(echoed ? "ECHO_CHAR_OK" : "ECHO_CHAR_RETRY",
                        target.EntityId, target.Text, "char=" + character + "; attempt=" + attempt +
                        "; expected=" + expected + "; actual=" + CurrentInputText());
                    if (!echoed)
                        GameInputSender.WakeKeyboundInput(current);
                }
                if (!echoed)
                    return result == GameInputResult.Sent ? GameInputResult.NativeInputFailed : result;
                if (characterDelay != 0)
                {
                    int charactersLeft = typedText.Length - characterIndex - 1;
                    int budgetedDelay = charactersLeft == 0 ? 0 :
                        Math.Max(0, (RemainingMilliseconds(submitDeadlineUtc) - 75) /
                        (charactersLeft + 1));
                    int delay = Math.Min(characterDelay, budgetedDelay);
                    if (delay != 0) System.Threading.Thread.Sleep(delay);
                }
            }
            return WaitForInputText(typedText,
                Math.Min(150, RemainingMilliseconds(submitDeadlineUtc)), current)
                ? GameInputResult.Sent
                : GameInputResult.NativeInputFailed;
        }

        private GameInputResult EnsureKeyboundInputFocused(KtgMemoryTarget target, Func<bool> current)
        {
            KtgInputState state = memoryReader == null ? null : memoryReader.ReadInputState();
            if (state != null && state.Available && state.Visible && state.Focused)
            {
                WriteAutomationDiagnostic("FOCUS_ALREADY", target.EntityId, target.Text,
                    "visible=" + state.Visible + "; focused=true");
                return GameInputResult.Sent;
            }
            if (state != null && state.Available && !String.IsNullOrEmpty(state.Text))
            {
                WriteAutomationDiagnostic("FOCUS_RECOVER_STALE", target.EntityId, target.Text,
                    "visible=" + state.Visible + "; text=" + state.Text);
            }
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                GameInputResult result = GameInputSender.SubmitEnter(current);
                WriteAutomationDiagnostic("FOCUS_ENTER", target.EntityId, target.Text,
                    "attempt=" + attempt + "; before=" + DescribeInputState(state) +
                    "; result=" + result);
                if (result != GameInputResult.Sent)
                    return result;
                if (WaitForInputFocus(800, current))
                    return GameInputResult.Sent;
                state = memoryReader == null ? null : memoryReader.ReadInputState();
                WriteAutomationDiagnostic("FOCUS_RETRY", target.EntityId, target.Text,
                    "attempt=" + attempt + "; after=" + DescribeInputState(state));
                if (attempt != 2)
                    System.Threading.Thread.Sleep(250);
            }
            return GameInputResult.NativeInputFailed;
        }

        private static string DescribeInputState(KtgInputState state)
        {
            return state == null ? "null" : "available=" + state.Available +
                "; visible=" + state.Visible + "; focused=" + state.Focused +
                "; text=" + (state.Text ?? String.Empty);
        }

        private bool WaitForInputFocus(int timeoutMilliseconds, Func<bool> current)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            do
            {
                if (!current()) return false;
                KtgInputState state = memoryReader == null ? null : memoryReader.ReadInputState();
                if (state != null && state.Available && state.Visible && state.Focused)
                    return true;
                System.Threading.Thread.Sleep(15);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }

        private bool ClearInputWithEcho(KtgMemoryTarget target, string existing, Func<bool> current,
            DateTime submitDeadlineUtc)
        {
            string text = existing ?? String.Empty;
            for (int index = 0; index < text.Length; index++)
            {
                if (!current() || RemainingMilliseconds(submitDeadlineUtc) <= 0 ||
                    GameInputSender.PostBackspaceToFfxiv(current) != GameInputResult.Sent)
                    return false;
                System.Threading.Thread.Sleep(25);
            }
            bool cleared = WaitForInputText(String.Empty,
                Math.Min(400, RemainingMilliseconds(submitDeadlineUtc)), current);
            WriteAutomationDiagnostic(cleared ? "CLEAR_OK" : "CLEAR_FAIL", target.EntityId,
                target.Text, "before=" + text + "; after=" + CurrentInputText());
            return cleared;
        }

        private bool WaitForInputText(string expected, int timeoutMilliseconds, Func<bool> current)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            do
            {
                if (!current()) return false;
                KtgInputState state = memoryReader == null ? null : memoryReader.ReadInputState();
                if (state != null && state.Available &&
                    String.Equals(state.Text, expected, StringComparison.Ordinal))
                    return true;
                System.Threading.Thread.Sleep(15);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }

        private string CurrentInputText()
        {
            KtgInputState state = memoryReader == null ? null : memoryReader.ReadInputState();
            return state == null || !state.Available ? "<unavailable>" : state.Text;
        }

        private static int RemainingMilliseconds(DateTime deadlineUtc)
        {
            return Math.Max(0, (int)Math.Floor((deadlineUtc - DateTime.UtcNow).TotalMilliseconds));
        }

        private static string DirectTargetKey(uint entityId, string text)
        {
            return entityId.ToString("X8") + ":" + (text ?? String.Empty);
        }

        private void RemoveDirectFailuresForEntity(uint entityId)
        {
            string prefix = entityId.ToString("X8") + ":";
            var remove = new List<string>();
            foreach (string key in directFailureCounts.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    remove.Add(key);
            foreach (string key in remove)
                directFailureCounts.Remove(key);
        }

        private static int RandomDelay(Random random, int minimum, int maximum)
        {
            return maximum <= minimum ? minimum : random.Next(minimum, maximum + 1);
        }

        private static int NextActivityPulseDelay(bool initial)
        {
            var random = new Random(unchecked(Environment.TickCount * 397));
            return initial ? random.Next(20000, 35001) : random.Next(25000, 55001);
        }

        private void TryStartActivityPulse()
        {
            if (autoInputEnabled == null || !autoInputEnabled.Checked || !keyboundAddonPresent ||
                directAttemptActive || activityPulseActive || DateTime.UtcNow < nextActivityPulseUtc)
                return;
            KtgInputState state = memoryReader == null ? null : memoryReader.ReadInputState();
            if (state == null || !state.Available || state.Focused || !String.IsNullOrEmpty(state.Text))
            {
                nextActivityPulseUtc = DateTime.UtcNow.AddSeconds(2);
                return;
            }

            long sequence = System.Threading.Interlocked.Increment(ref activityPulseSequence);
            int generation = System.Threading.Interlocked.CompareExchange(
                ref activityPulseGeneration, 0, 0);
            activityPulseActive = true;
            nextActivityPulseUtc = DateTime.UtcNow.AddMilliseconds(NextActivityPulseDelay(false));
            int digit = new Random(unchecked(Environment.TickCount * 31 + (int)sequence)).Next(0, 10);
            string expected = digit.ToString();
            WriteAutomationDiagnostic("ACTIVITY_START", 0, String.Empty,
                "physical wrong attempt; digit=" + expected);

            var worker = new System.Threading.Thread((System.Threading.ThreadStart)delegate
            {
                Func<bool> current = delegate
                {
                    return System.Threading.Interlocked.CompareExchange(
                        ref activityPulseGeneration, 0, 0) == generation &&
                        System.Threading.Interlocked.Read(ref activityPulseSequence) == sequence;
                };
                GameInputResult result = GameInputSender.SubmitEnter(current);
                bool focused = result == GameInputResult.Sent && WaitForInputFocus(500, current);
                bool echoed = false;
                bool cleared = false;
                if (focused)
                {
                    result = GameInputSender.SendPhysicalVirtualKey((ushort)('0' + digit), current);
                    echoed = result == GameInputResult.Sent && WaitForInputText(expected, 500, current);
                    if (echoed)
                    {
                        result = GameInputSender.SubmitEnter(current);
                        cleared = result == GameInputResult.Sent &&
                            WaitForInputText(String.Empty, 650, current);
                    }
                    else
                    {
                        GameInputSender.SendPhysicalVirtualKey(0x08, current);
                        GameInputSender.SubmitEnter(current);
                    }
                }
                WriteAutomationDiagnostic(cleared ? "ACTIVITY_OK" : "ACTIVITY_FAIL", 0,
                    String.Empty, "digit=" + expected + "; result=" + result +
                    "; focused=" + focused + "; echoed=" + echoed + "; cleared=" + cleared +
                    "; actual=" + CurrentInputText());
                if (autoInputStatus == null || autoInputStatus.IsDisposed)
                    return;
                try
                {
                    autoInputStatus.BeginInvoke((Action)(delegate
                    {
                        if (System.Threading.Interlocked.Read(ref activityPulseSequence) != sequence ||
                            System.Threading.Interlocked.CompareExchange(
                                ref activityPulseGeneration, 0, 0) != generation)
                            return;
                        activityPulseActive = false;
                        directRetryAfterUtc = DateTime.UtcNow.AddMilliseconds(120);
                        autoInputStatus.Text = cleared
                            ? "自动输入：已发送一次随机真实按键活动"
                            : "自动输入：随机真实按键未完成，稍后重试";
                    }));
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
        }

        private void WriteAutomationDiagnostic(string kind, uint entityId, string text, string detail)
        {
            if (String.IsNullOrEmpty(automationDiagnosticPath))
                return;
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + kind +
                    "\t0x" + entityId.ToString("X8") + "\t" + (text ?? String.Empty) +
                    "\t" + (detail ?? String.Empty) + Environment.NewLine;
                lock (automationDiagnosticLock)
                {
                    lastAutomationEventKind = kind ?? String.Empty;
                    lastAutomationEventDetail = detail ?? String.Empty;
                    lastAutomationEventTime = DateTime.Now;
                    File.AppendAllText(automationDiagnosticPath, line, new System.Text.UTF8Encoding(false));
                }
            }
            catch { }
        }

        private void OnOverlayAutomationRequested(bool enabled)
        {
            if (autoInputEnabled == null || autoInputEnabled.IsDisposed)
                return;
            try
            {
                autoInputEnabled.BeginInvoke((Action)(delegate
                {
                    autoInputEnabled.Checked = enabled;
                    PublishOverlayState();
                }));
            }
            catch { }
        }

        private void PublishOverlayState()
        {
            if (overlayServer == null)
                return;
            KtgMemoryTarget displayTarget = null;
            foreach (KtgMemoryTarget candidate in memoryTargets.Values)
            {
                if (displayTarget == null || combatPlanner.CompareTargets(
                    candidate.EntityId, candidate.Slot, displayTarget.EntityId, displayTarget.Slot,
                    directFocusEntityId, DateTime.UtcNow) < 0)
                    displayTarget = candidate;
            }
            uint entityId = directAttemptActive ? directTargetId :
                (displayTarget == null ? 0U : displayTarget.EntityId);
            string targetText = directAttemptActive ? directTargetText :
                (displayTarget == null ? String.Empty : displayTarget.Text);
            string attemptState = autoInputEnabled != null && !autoInputEnabled.Checked ? "已停用" :
                directAwaitingConfirmation ? "等待攻击确认" :
                directAttemptActive ? "正在输入" : overlayTargetCount == 0 ? "等待题目" : "准备提交";
            string eventKind;
            string eventDetail;
            string eventTime;
            lock (automationDiagnosticLock)
            {
                eventKind = lastAutomationEventKind;
                eventDetail = lastAutomationEventDetail;
                eventTime = lastAutomationEventTime.ToString("HH:mm:ss.fff");
            }
            var json = new System.Text.StringBuilder(1024);
            json.Append("{\"Version\":1,\"Ready\":true")
                .Append(",\"UpdatedAt\":").Append(KeyboundOverlayServer.JsonString(DateTime.Now.ToString("o")))
                .Append(",\"NetworkConnected\":").Append(networkTap != null && networkTap.IsConnected ? "true" : "false")
                .Append(",\"MemoryStatus\":").Append(KeyboundOverlayServer.JsonString(overlayMemoryStatus))
                .Append(",\"TargetCount\":").Append(overlayTargetCount)
                .Append(",\"InputVisible\":").Append(overlayInputVisible ? "true" : "false")
                .Append(",\"InputFocused\":").Append(overlayInputFocused ? "true" : "false")
                .Append(",\"InputText\":").Append(KeyboundOverlayServer.JsonString(overlayInputText))
                .Append(",\"InputDiagnostic\":").Append(KeyboundOverlayServer.JsonString(overlayInputDiagnostic))
                .Append(",\"AutomationEnabled\":").Append(autoInputEnabled != null && autoInputEnabled.Checked ? "true" : "false")
                .Append(",\"AutomationStatus\":").Append(KeyboundOverlayServer.JsonString(autoInputStatus == null ? String.Empty : autoInputStatus.Text))
                .Append(",\"AttemptState\":").Append(KeyboundOverlayServer.JsonString(attemptState))
                .Append(",\"EntityId\":").Append(KeyboundOverlayServer.JsonString(entityId == 0 ? String.Empty : "0x" + entityId.ToString("X8")))
                .Append(",\"TargetText\":").Append(KeyboundOverlayServer.JsonString(targetText ?? String.Empty))
                .Append(",\"AwaitingConfirmation\":").Append(directAwaitingConfirmation ? "true" : "false")
                .Append(",\"BudgetMilliseconds\":").Append(GameInputSender.DirectTypingToSubmitBudgetMs)
                .Append(",\"Score\":").Append(KeyboundOverlayServer.JsonString(scoreLabel == null ? "分数：—" : scoreLabel.Text))
                .Append(",\"Phase\":").Append(KeyboundOverlayServer.JsonString(phaseLabel == null ? "阶段：—" : phaseLabel.Text))
                .Append(",\"LastEvent\":").Append(KeyboundOverlayServer.JsonString(eventKind))
                .Append(",\"LastEventDetail\":").Append(KeyboundOverlayServer.JsonString(eventDetail))
                .Append(",\"LastEventTime\":").Append(KeyboundOverlayServer.JsonString(eventTime))
                .Append('}');
            overlayServer.SetState(json.ToString());
        }

#if LEGACY_DALAMUD_BRIDGE
        private void OnBridgeConnectionChanged(bool connected)
        {
            if (bridgeStatusLabel == null || bridgeStatusLabel.IsDisposed)
                return;
            bridgeStatusLabel.BeginInvoke((Action)(delegate
            {
                bridgeStatusLabel.Text = connected
                    ? "Dalamud 桥接：已连接，等待可见题目。"
                    : "Dalamud 桥接：未连接，请确认卫月开发插件已加载。";
                if (autoInputEnabled != null)
                    autoInputEnabled.Enabled = connected;
                if (!connected && autoInputEnabled != null && autoInputEnabled.Checked)
                {
                    autoInputEnabled.Checked = false;
                    autoInputStatus.Text = autoAttemptActive
                        ? "自动输入：桥接在提交期间断开，已暂停"
                        : "自动输入：桥接未连接，已停用";
                    return;
                }
                if (connected && autoInputEnabled != null && autoInputEnabled.Checked)
                    bridgeClient.SetArmed(true);
            }));
        }

        private void OnBridgeSnapshotReceived(KeyboundBridgeSnapshot snapshot)
        {
            if (activePrompts == null || activePrompts.IsDisposed)
                return;
            activePrompts.BeginInvoke((Action)(delegate { ApplyBridgeSnapshot(snapshot); }));
        }

        private void ApplyBridgeSnapshot(KeyboundBridgeSnapshot snapshot)
        {
            bridgeSnapshot = snapshot;
            var visible = new HashSet<uint>();
            snapshot.Targets.Sort(delegate(KeyboundBridgeTarget left, KeyboundBridgeTarget right)
            {
                string order = automationSettings == null ? "slot_asc" : automationSettings.TargetOrder;
                if (order == "slot_desc")
                    return right.Slot.CompareTo(left.Slot);
                if (order == "shortest")
                {
                    int length = left.Text.Length.CompareTo(right.Text.Length);
                    return length != 0 ? length : left.Slot.CompareTo(right.Slot);
                }
                if (order == "longest")
                {
                    int length = right.Text.Length.CompareTo(left.Text.Length);
                    return length != 0 ? length : left.Slot.CompareTo(right.Slot);
                }
                return left.Slot.CompareTo(right.Slot);
            });

            foreach (KeyboundBridgeTarget target in snapshot.Targets)
            {
                visible.Add(target.EntityId);
                KeyboundBridgeTarget previous;
                if (bridgeTargets.TryGetValue(target.EntityId, out previous) &&
                    !String.Equals(previous.Text, target.Text, StringComparison.Ordinal))
                    RemoveSubmittedForMonster(target.EntityId);
                bridgeTargets[target.EntityId] = target;

                ListViewItem item;
                if (!activeItems.TryGetValue(target.EntityId, out item))
                {
                    item = new ListViewItem();
                    item.SubItems.Add(String.Empty);
                    item.SubItems.Add(String.Empty);
                    item.SubItems.Add(String.Empty);
                    item.SubItems.Add(String.Empty);
                    activeItems[target.EntityId] = item;
                    activePrompts.Items.Add(item);
                }
                item.Text = "0x" + target.EntityId.ToString("X8");
                item.SubItems[1].Text = "槽位 " + target.Slot;
                item.SubItems[2].Text = target.Text;
                item.SubItems[3].Text = target.Text;
                item.SubItems[4].Text = target.Obstructed ? "被遮挡" : "可提交";
            }

            var removed = new List<uint>();
            foreach (uint entityId in bridgeTargets.Keys)
                if (!visible.Contains(entityId))
                    removed.Add(entityId);
            foreach (uint entityId in removed)
            {
                bridgeTargets.Remove(entityId);
                queuedAutoPrompts.Remove(entityId);
                queuedAutoMonsters.Remove(entityId);
                RemoveSubmittedForMonster(entityId);
                ListViewItem item;
                if (activeItems.TryGetValue(entityId, out item))
                {
                    activeItems.Remove(entityId);
                    activePrompts.Items.Remove(item);
                }
            }

            bridgeStatusLabel.Text = "Dalamud 桥接：已连接；区域 " + snapshot.TerritoryId +
                "；可见 " + snapshot.Targets.Count + "；输入框 " +
                (snapshot.InputReady ? "就绪" : "忙碌") + "；授权 " +
                (snapshot.Armed ? "开" : "关") + "。";

            if (autoInputEnabled != null && autoInputEnabled.Checked && snapshot.Armed && snapshot.InputReady)
            {
                foreach (KeyboundBridgeTarget target in snapshot.Targets)
                    TryScheduleAutoInput(target);
                StartNextAutoInput();
            }
        }

        private void OnBridgeSubmitResultReceived(KeyboundBridgeSubmitResult result)
        {
            if (autoInputStatus == null || autoInputStatus.IsDisposed)
                return;
            autoInputStatus.BeginInvoke((Action)(delegate { ApplyBridgeSubmitResult(result); }));
        }

        private void ApplyBridgeSubmitResult(KeyboundBridgeSubmitResult result)
        {
            if (!autoAttemptActive || result.RequestId != autoBridgeRequestId ||
                result.EntityId != autoAttemptMonsterId)
                return;

            if (result.Success)
            {
                if (autoAttemptTargetDied)
                {
                    autoInputStatus.Text = "自动输入：提交完成，目标同时死亡";
                    FinishCurrentAutoAttempt();
                    StartNextAutoInput();
                    return;
                }
                autoAwaitingConfirmation = true;
                autoConfirmationDeadlineUtc = DateTime.UtcNow.AddSeconds(3);
                autoInputStatus.Text = "自动输入：游戏已接受 “" +
                    (bridgeTargets.ContainsKey(result.EntityId) ? bridgeTargets[result.EntityId].Text : String.Empty) +
                    "”，等待 ACT 攻击确认";
                return;
            }

            string failedKey = autoAttemptKey;
            submittedPrompts.Remove(failedKey);
            FinishCurrentAutoAttempt();
            autoInputEnabled.Checked = false;
            autoInputStatus.Text = "自动输入：桥接拒绝提交，已暂停（" + result.Message + "）";
        }

        private void RemoveSubmittedForMonster(uint entityId)
        {
            string prefix = entityId.ToString("X8") + ":";
            submittedPrompts.RemoveWhere(delegate(string value) { return value.StartsWith(prefix, StringComparison.Ordinal); });
        }
#endif

        private void ApplyCombatSignal(KeyboundCombatSignal signal)
        {
            if (signal == null)
                return;
            if (signal.Kind == KeyboundCombatSignalKind.ActorAdded)
            {
                if (!KeyboundCombatPlanner.IsRelevantActorType(signal.BNpcId))
                    return;
                int previousStage = combatPlanner.CombatStage;
                combatPlanner.ObserveActor(signal.EntityId, signal.BNpcId);
                phaseLabel.Text = "阶段：P" + combatPlanner.CombatStage +
                    " / 网络 0x" + combatPlanner.DirectorPhase.ToString("X");
                priorityLabel.Text = "优先级：" + combatPlanner.Describe();
                WriteAutomationDiagnostic("ACTOR_ADD", signal.EntityId, String.Empty,
                    "bnpc=0x" + signal.BNpcId.ToString("X") + "; maxHp=" + signal.MaxHp +
                    "; stage=P" + combatPlanner.CombatStage);
                if (previousStage != combatPlanner.CombatStage)
                    WriteAutomationDiagnostic("COMBAT_STAGE", signal.EntityId, String.Empty,
                        "P" + previousStage + " -> P" + combatPlanner.CombatStage +
                        "; bnpc=0x" + signal.BNpcId.ToString("X"));
                return;
            }
            if (signal.Kind == KeyboundCombatSignalKind.CastStarted)
            {
                DateTime observedUtc = DateTime.UtcNow;
                DateTime castStartUtc = KeyboundCombatPlanner.ResolveCastStartTime(
                    signal.ActionId, signal.UtcTime, observedUtc);
                combatPlanner.RecordCastStart(signal.SourceId, signal.ActionId,
                    signal.CastSeconds, castStartUtc);
                priorityLabel.Text = "优先级：" + combatPlanner.Describe();
                WriteAutomationDiagnostic("CAST_START", signal.SourceId, signal.ActionName,
                    "action=0x" + signal.ActionId.ToString("X") + "; castSeconds=" +
                    signal.CastSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                    "; eventLagMs=" + Math.Max(0, (int)(observedUtc - signal.UtcTime).TotalMilliseconds) +
                    "; timing=" + (KeyboundCombatPlanner.IsStopCastAction(signal.ActionId)
                        ? "observed" : "event") +
                    "; bnpc=0x" + combatPlanner.GetActorType(signal.SourceId).ToString("X"));
                return;
            }
            if (signal.Kind == KeyboundCombatSignalKind.CastEnded)
            {
                DateTime observedUtc = DateTime.UtcNow;
                combatPlanner.RecordCastEnd(signal.SourceId, signal.ActionId,
                    signal.Interrupted, observedUtc);
                WriteAutomationDiagnostic(signal.Interrupted ? "CAST_INTERRUPTED" : "CAST_END",
                    signal.SourceId, signal.ActionName,
                    "action=0x" + signal.ActionId.ToString("X") +
                    "; eventLagMs=" + Math.Max(0,
                        (int)(observedUtc - signal.UtcTime).TotalMilliseconds) +
                    "; resumeGuardMs=2000");
                return;
            }
            if (signal.Kind == KeyboundCombatSignalKind.StatusAdded ||
                signal.Kind == KeyboundCombatSignalKind.StatusRemoved)
            {
                bool vision = signal.StatusId == 0x14E4 || signal.StatusId == 0x1560;
                if (!vision)
                    return;
                if (signal.Kind == KeyboundCombatSignalKind.StatusAdded)
                {
                    DateTime until = signal.UtcTime.AddSeconds(Math.Max(1, signal.DurationSeconds));
                    if (until > visualInterferenceUntilUtc)
                        visualInterferenceUntilUtc = until;
                    lastVisualObservationSignature = null;
                }
                WriteAutomationDiagnostic(signal.Kind == KeyboundCombatSignalKind.StatusAdded
                        ? "VISION_STATUS_ON" : "VISION_STATUS_OFF",
                    signal.SourceId, signal.StatusName,
                    "status=0x" + signal.StatusId.ToString("X") +
                    "; target=0x" + signal.TargetId.ToString("X8") +
                    "; duration=" + signal.DurationSeconds.ToString("0.00",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    "; targetsVisible=" + overlayTargetCount +
                    "; inputVisible=" + overlayInputVisible);
            }
        }

        private void ApplyDecodedEvent(KeyboundEvent value)
        {
            if (value.Kind == KeyboundEventKind.Score)
            {
                scoreLabel.Text = "分数：" + value.Score;
                AddRecent(value, "分数", "更新为 " + value.Score);
                return;
            }
            if (value.Kind == KeyboundEventKind.Phase)
            {
                combatPlanner.SetDirectorPhase(value.Phase);
                WriteAutomationDiagnostic("PHASE", 0, String.Empty, "0x" + value.Phase.ToString("X"));
                phaseLabel.Text = "阶段：P" + combatPlanner.CombatStage +
                    " / 网络 0x" + value.Phase.ToString("X");
                priorityLabel.Text = "优先级：" + combatPlanner.Describe();
                AddRecent(value, "阶段", "0x" + value.Phase.ToString("X"));
                if (value.Phase == 0 || value.Phase == 4 || value.Phase == 5)
                {
                    ResetDirectAutomation(true);
                    memoryTargets.Clear();
                    activeItems.Clear();
                    activePrompts.Items.Clear();
                }
                return;
            }

            string display = value.Word == null ? "<未解析>" : value.Word.DisplayText;
            string input = value.Word == null ? String.Empty :
                value.Word.InputKeys + " / " + value.Word.InputText;
            if (value.Kind == KeyboundEventKind.Prompt)
            {
                AddNetworkPromptRecent(value);
            }
            else if (value.Kind == KeyboundEventKind.InputSuccess)
            {
                ListViewItem item;
                if (activeItems.TryGetValue(value.MonsterId, out item))
                {
                    item.SubItems[4].Text = "输入成功";
                    display = item.SubItems[2].Text;
                    input = item.SubItems[3].Text;
                }
                AddRecent(value, "成功", input);
                ConfirmDirectAttack(value.MonsterId, "攻击成功");
            }
            else if (value.Kind == KeyboundEventKind.MonsterDied)
            {
                int phaseOneKillsBefore = combatPlanner.PhaseOneKillCount;
                bool firstDeath = combatPlanner.MarkDied(value.MonsterId);
                ListViewItem item;
                if (activeItems.TryGetValue(value.MonsterId, out item))
                {
                    display = item.SubItems[2].Text;
                    input = item.SubItems[3].Text;
                    activeItems.Remove(value.MonsterId);
                    activePrompts.Items.Remove(item);
                }
                AddRecent(value, "死亡", input);
                if (firstDeath)
                {
                    priorityLabel.Text = "优先级：" + combatPlanner.Describe();
                    if (combatPlanner.PhaseOneKillCount > phaseOneKillsBefore)
                        WriteAutomationDiagnostic("P1_KILL", value.MonsterId, display,
                            combatPlanner.PhaseOneKillCount + "/" + KeyboundCombatPlanner.PhaseOneKillGoal);
                }
                ConfirmDirectAttack(value.MonsterId, "目标死亡");
            }

            decoderStatusLabel.Text = "KTGEWord 直读；最近网络诊断事件 " +
                DateTime.Now.ToString("HH:mm:ss") + "。";
        }

        private void AddRecent(KeyboundEvent value, string eventName, string detail)
        {
            string wordId = value.WordId == 0 ? String.Empty : value.WordId.ToString();
            string display = value.Word == null ? String.Empty : value.Word.DisplayText;
            var item = new ListViewItem(value.Time.ToString("HH:mm:ss.fff"));
            item.SubItems.Add(eventName);
            item.SubItems.Add(value.MonsterId == 0 ? String.Empty : "0x" + value.MonsterId.ToString("X8"));
            item.SubItems.Add(wordId);
            item.SubItems.Add(display);
            item.SubItems.Add(detail ?? String.Empty);
            recentEvents.Items.Insert(0, item);
            while (recentEvents.Items.Count > 200)
                recentEvents.Items.RemoveAt(recentEvents.Items.Count - 1);
        }

        private void AddNetworkPromptRecent(KeyboundEvent value)
        {
            string wordId = value.WordId == 0 ? String.Empty : value.WordId.ToString();
            var item = new ListViewItem(value.Time.ToString("HH:mm:ss.fff"));
            item.SubItems.Add("网络出题");
            item.SubItems.Add(value.MonsterId == 0 ? String.Empty : "0x" + value.MonsterId.ToString("X8"));
            item.SubItems.Add(wordId);
            item.SubItems.Add(String.Empty);
            item.SubItems.Add("全队网络事件，不作为本地题目或自动攻击来源");
            recentEvents.Items.Insert(0, item);
            while (recentEvents.Items.Count > 200)
                recentEvents.Items.RemoveAt(recentEvents.Items.Count - 1);
        }

#if LEGACY_DALAMUD_BRIDGE
        private void TryScheduleAutoInput(KeyboundBridgeTarget value)
        {
            if (autoInputEnabled == null || !autoInputEnabled.Checked ||
                value == null || value.Obstructed || String.IsNullOrWhiteSpace(value.Text))
                return;

            string key = value.EntityId.ToString("X8") + ":" + value.Text;
            if (!submittedPrompts.Add(key))
                return;

            queuedAutoPrompts[value.EntityId] = value;
            if (queuedAutoMonsters.Add(value.EntityId))
                autoPromptOrder.Enqueue(value.EntityId);
            autoInputStatus.Text = "自动输入：队列 " + queuedAutoPrompts.Count + " 只怪物";
        }

        private void StartNextAutoInput()
        {
            if (autoAttemptActive || autoInputEnabled == null || !autoInputEnabled.Checked)
                return;

            if (bridgeClient == null || !bridgeClient.IsConnected || bridgeSnapshot == null ||
                !bridgeSnapshot.Armed || !bridgeSnapshot.InputReady)
            {
                autoInputStatus.Text = "自动输入：等待 Dalamud 桥接和输入框就绪";
                return;
            }

            KeyboundBridgeTarget value = null;
            while (autoPromptOrder.Count != 0)
            {
                uint monsterId = autoPromptOrder.Dequeue();
                queuedAutoMonsters.Remove(monsterId);
                if (queuedAutoPrompts.TryGetValue(monsterId, out value))
                {
                    queuedAutoPrompts.Remove(monsterId);
                    break;
                }
                value = null;
            }
            if (value == null)
            {
                autoInputStatus.Text = "自动输入：等待中文题目";
                return;
            }

            string text = value.Text;
            if (String.IsNullOrEmpty(text))
            {
                submittedPrompts.Remove(value.EntityId.ToString("X8") + ":" + value.Text);
                StartNextAutoInput();
                return;
            }
            string display = value.Text;
            string key = value.EntityId.ToString("X8") + ":" + value.Text;
            int cadenceDelay = automationSettings.NextEquivalentTypingDelay(text,
                new Random(unchecked(Environment.TickCount * 31 + (int)(autoAttemptSequence & 0x7FFFFFFF))));
            int generation = System.Threading.Interlocked.CompareExchange(
                ref autoInputGeneration, 0, 0);
            long sequence = System.Threading.Interlocked.Increment(ref autoAttemptSequence);
            autoAttemptActive = true;
            autoAwaitingConfirmation = false;
            autoAttemptMonsterId = value.EntityId;
            autoAttemptKey = key;
            autoAttemptTargetDied = false;
            string requestId = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            autoBridgeRequestId = requestId;
            autoInputStatus.Text = "自动输入：模拟打字 “" + display + "”，约 " + cadenceDelay + "ms";
            var worker = new System.Threading.Thread((System.Threading.ThreadStart)delegate
            {
                if (cadenceDelay > 0)
                    System.Threading.Thread.Sleep(cadenceDelay);
                bool current = System.Threading.Interlocked.CompareExchange(
                    ref autoInputGeneration, 0, 0) == generation &&
                    System.Threading.Interlocked.Read(ref autoAttemptSequence) == sequence;
                bool sent = current && bridgeClient != null &&
                    bridgeClient.Submit(requestId, value.EntityId, value.Text);
                if (autoInputStatus == null || autoInputStatus.IsDisposed)
                    return;
                autoInputStatus.BeginInvoke((Action)(delegate
                {
                    if (System.Threading.Interlocked.Read(ref autoAttemptSequence) != sequence)
                        return;
                    if (!sent)
                    {
                        submittedPrompts.Remove(key);
                        FinishCurrentAutoAttempt();
                        autoInputEnabled.Checked = false;
                        autoInputStatus.Text = "自动输入：无法向 Dalamud 桥接发送请求，已暂停";
                    }
                    else
                    {
                        autoConfirmationDeadlineUtc = DateTime.UtcNow.AddSeconds(3);
                        autoInputStatus.Text = "自动输入：已发送 “" + display + "”，等待游戏接受";
                    }
                }));
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void ConfirmAutoAttack(uint monsterId, string confirmation)
        {
            if (!autoAttemptActive || !autoAwaitingConfirmation ||
                autoAttemptMonsterId != monsterId)
                return;
            autoInputStatus.Text = "自动输入：" + confirmation + " 0x" + monsterId.ToString("X8");
            FinishCurrentAutoAttempt();
            StartNextAutoInput();
        }

        private void FinishCurrentAutoAttempt()
        {
            System.Threading.Interlocked.Increment(ref autoAttemptSequence);
            autoAttemptActive = false;
            autoAwaitingConfirmation = false;
            autoAttemptMonsterId = 0;
            autoAttemptKey = null;
            autoAttemptTargetDied = false;
            autoBridgeRequestId = null;
            autoConfirmationDeadlineUtc = DateTime.MinValue;
        }

        private void ClearAutomationQueue(bool clearSubmitted)
        {
            System.Threading.Interlocked.Increment(ref autoAttemptSequence);
            autoPromptOrder.Clear();
            queuedAutoPrompts.Clear();
            queuedAutoMonsters.Clear();
            autoAttemptActive = false;
            autoAwaitingConfirmation = false;
            autoAttemptMonsterId = 0;
            autoAttemptKey = null;
            autoAttemptTargetDied = false;
            autoBridgeRequestId = null;
            autoConfirmationDeadlineUtc = DateTime.MinValue;
            if (clearSubmitted)
                submittedPrompts.Clear();
        }
#endif

        private void RefreshUi()
        {
            TryStartActivityPulse();
            ScheduleMemoryPoll();
            if (directAttemptActive && directAwaitingConfirmation &&
                directConfirmationDeadlineUtc != DateTime.MinValue &&
                DateTime.UtcNow >= directConfirmationDeadlineUtc)
            {
                string failed = directTargetText;
                uint failedId = directTargetId;
                string failedKey = DirectTargetKey(failedId, failed);
                int failures;
                directFailureCounts.TryGetValue(failedKey, out failures);
                failures++;
                directFailureCounts[failedKey] = failures;
                ResetDirectAutomation(false);
                directRetryAfterUtc = DateTime.UtcNow.AddMilliseconds(500);
                if (failures >= 3)
                {
                    directCompletedPrompts.MarkCompleted(failedId, failed);
                    autoInputStatus.Text = "自动输入：连续 3 次未确认，暂时跳过 “" + failed + "”";
                    WriteAutomationDiagnostic("DEMOTE", failedId, failed,
                        "memory-confirmation-timeout; failures=" + failures);
                }
                else
                {
                    autoInputStatus.Text = "自动输入：未收到攻击确认，将重试 “" + failed + "”（" +
                        failures + "/3）";
                    WriteAutomationDiagnostic("TIMEOUT_RETRY", failedId, failed,
                        "memory-confirmation-timeout; failures=" + failures);
                }
            }
            bool captureNeedsPackets = capture != null && capture.IsAccepting &&
                DateTime.UtcNow - capture.StartedUtc > TimeSpan.FromSeconds(5) &&
                networkTap != null && networkTap.LastPacketUtc < capture.StartedUtc;
            if (networkTap != null && (!networkTap.IsConnected || !networkTap.IsCurrentSubscription() || captureNeedsPackets) &&
                DateTime.UtcNow >= nextConnectAttemptUtc)
            {
                nextConnectAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                networkTap.Connect();
            }
            bool connected = networkTap != null && networkTap.IsConnected;
            connectionLabel.Text = connected
                ? "FFXIV_ACT_Plugin：已连接" +
                    (networkTap.LastPacketUtc == DateTime.MinValue ? "，等待原始入站包。" :
                    "；最后网络包 " + networkTap.LastPacketUtc.ToLocalTime().ToString("HH:mm:ss") + "。")
                : "FFXIV_ACT_Plugin：未连接。" + (networkTap == null ? String.Empty : " " + networkTap.Error);

            bool running = capture != null && capture.IsAccepting;
            startButton.Enabled = connected && !running;
            stopButton.Enabled = running;
            foreach (Button button in markerButtons)
                button.Enabled = running;

            if (running)
            {
                TimeSpan elapsed = DateTime.UtcNow - capture.StartedUtc;
                double mebibytes = capture.CurrentBinaryBytes / (1024.0 * 1024.0);
                captureLabel.Text = "连续捕获中：" + elapsed.ToString(@"hh\:mm\:ss") + "；" +
                    capture.AcceptedPackets + " 包；原始数据 " + mebibytes.ToString("F1") + " MiB；丢弃 " +
                    capture.DroppedPackets + (capture.WriterError == null ? String.Empty : "；写入错误：" + capture.WriterError);
            }
            else
            {
                captureLabel.Text = "尚未开始捕获。";
            }
            PublishOverlayState();
        }
    }

    internal sealed class FfxivNetworkTap : IDisposable
    {
        private readonly Action<string, long, byte[]> receiver;
        private object subscription;
        private EventInfo networkEvent;
        private Delegate networkHandler;

        public FfxivNetworkTap(Action<string, long, byte[]> receiver)
        {
            this.receiver = receiver;
        }

        public DateTime LastPacketUtc { get; private set; }

        public bool IsConnected
        {
            get
            {
                return subscription != null && networkEvent != null && networkHandler != null;
            }
        }
        public string Error { get; private set; }

        public bool Connect()
        {
            Disconnect();
            try
            {
                object plugin = FindFfxivPlugin();
                if (plugin == null)
                {
                    Error = "未找到已启用的 FFXIV_ACT_Plugin。请确认插件加载顺序。";
                    return false;
                }

                subscription = plugin.GetType().GetProperty("DataSubscription").GetValue(plugin, null);
                if (subscription == null)
                {
                    Error = "FFXIV_ACT_Plugin DataSubscription 尚未就绪。";
                    return false;
                }

                networkEvent = subscription.GetType().GetEvent("NetworkReceived");
                if (networkEvent == null)
                {
                    Error = "当前 FFXIV_ACT_Plugin 没有 NetworkReceived 事件。";
                    Disconnect();
                    return false;
                }

                MethodInfo callback = GetType().GetMethod("HandleNetworkReceived",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                networkHandler = Delegate.CreateDelegate(networkEvent.EventHandlerType, this, callback);
                networkEvent.AddEventHandler(subscription, networkHandler);

                Error = null;
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.GetType().Name + ": " + ex.Message;
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            if (networkEvent != null && subscription != null && networkHandler != null)
            {
                try { networkEvent.RemoveEventHandler(subscription, networkHandler); }
                catch { }
            }
            networkEvent = null;
            networkHandler = null;
            subscription = null;
        }

        public bool IsCurrentSubscription()
        {
            if (subscription == null)
                return false;
            try
            {
                object plugin = FindFfxivPlugin();
                object current = plugin == null ? null :
                    plugin.GetType().GetProperty("DataSubscription").GetValue(plugin, null);
                return Object.ReferenceEquals(subscription, current);
            }
            catch { return false; }
        }

        public void Dispose()
        {
            Disconnect();
        }

        private static object FindFfxivPlugin()
        {
            foreach (ActPluginData data in ActGlobals.oFormActMain.ActPlugins)
            {
                if (data.pluginObj != null &&
                    data.pluginObj.GetType().FullName == "FFXIV_ACT_Plugin.FFXIV_ACT_Plugin")
                    return data.pluginObj;
            }
            return null;
        }

        private void HandleNetworkReceived(string connection, long epoch, byte[] message)
        {
            LastPacketUtc = DateTime.UtcNow;
            receiver(connection, epoch, message);
        }
    }
}
