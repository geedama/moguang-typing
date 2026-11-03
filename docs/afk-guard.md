# 防闲置实现与验证

旧日志中，通用 `InputTimerModule` 的输入计时持续为零，物理拼音也已发送，但对局仍触发了长时间无操作强退。因此这些信号不能当作魔光键影已解除闲置的证据。

本次参考 [KeyboundBrawler 的 AfkGuard](https://github.com/KEWI-hub/KeyboundBrawler/blob/5a0669c731ea93b7acb6fa5cc2a7ee3487854d8b/KeyboundBrawler/AfkGuard.cs) 所用的专用计时位置，以及 [AutoKeyboundBrawler](https://github.com/1Stalk/AutoKeyboundBrawler/blob/8603cd77487970250a1fb36d3dd6b37cbcb52ec0/Core/KTGAntiAfk.cs) 的直接内存重置方式。ACT 版本自行实现外部进程读写，不安装游戏内 Hook，也不需要 Dalamud。

## 写入条件

- 自动攻击已开启，且最近 30 秒有中文输入在 `KTGInput` 中回读成功。
- 当前区域为魔光键影（1359），当前 Director 类型为 23，且处于对局中。
- 从 `UIModule` 虚函数槽 37 的 getter 指令解析 AgentModule 偏移，再检查模块归属、初始化标记、Agent 397/398 的界面归属和虚表，以及输入 Agent 的 HUD 指针。
- 写入前再次检查区域、Director 和 Agent 关系；取消、切换进程或卸载插件后停止写入。

每次轮询满足条件时，只向输入 Agent `+0x50` / `+0x54` 的两个浮点计时值写零，共 8 字节。普通读取仍使用只读进程句柄；写入另开短期句柄，并核对进程创建时间以防 PID 被复用。写入后立即回读，允许一帧内的轻微计时增长。

本机国服的 AgentModule 偏移为 `0xE4AB8`，与部分公开结构的 `0xE4AC8` 不同。因此不能直接照搬固定模块偏移；getter 指令不匹配时会拒绝写入。自动攻击开启时若校验、写入或回读失败，会停用自动攻击和连续流程。

本机实战还观察到两个 Agent 使用主模块之外的重定向虚表，部分方法也被重定向；仅凭虚表地址在模块外就拒绝会误停自动攻击。对此要求两个 Agent 的 `+0x10` 均归属当前 UIModule，并校验虚表中本机观察到未重定向的第 2、4、6 槽仍指向游戏主模块。所有归属、HUD 关系和写入前复查继续保留。日志中的 `hudVtable`、`inputVtable` 和 `agentTables` 标明具体虚表及校验分支；未确认重定向来源。

## 日志与验收

`KeyboundProbe.auto.log` 中的 `AFK_GUARD` 在状态变化时和每 10 秒记录一次。开启捕获后，`.memory.tsv` 的 `BRAWLER_IDLE` 按内存轮询记录，包含进程、Director、Agent 地址，以及 `before` / `after` 两组计时值。

`reset` 只证明本次写入并回读成功；`observed` 表示仅观察，具体原因在 `reason`；`inactive` 表示尚未进入对局。`blocked`、`write-failed`、`readback-failed` 表示拒绝继续自动攻击。

离线测试覆盖写入范围、布局校验、近期活动门限、禁用/退出、取消、状态变化和写入/回读失败。修正虚表校验后的完整单局中，记录到 362 次成功重置，两个计时值写后回读均为 0；有效对局内未出现校验、写入或回读失败，最终正常结算退出。退本切换时的 3 次 `pointer-invalid` 发生在自动攻击关闭之后。连续多局仍待更多实战验证。

旧 CE 日志没有采集这两个专用计时值，不能用旧样本证明专用计时已被重置。
