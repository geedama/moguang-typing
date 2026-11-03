# CE 只读 AFK 日志

以管理员身份打开 Cheat Engine，启动 Bridge，并附加到 `ffxiv_dx11.exe`。
在魔光键影副本内，通过 CE Lua 环境运行：

```lua
dofile([[E:\actEnhanced\scripts\ce-keybound-afk-probe.lua]])
```

该脚本只读取游戏内存。CE 中的定时器负责采样，不注入游戏、不调用游戏函数、不设置断点。

每 500 ms 采样，记录到本机 ACT 的 `Plugins\KeyboundProbe\Captures\*.ce-afk.tsv`。
每次采样后刷新磁盘；离开区域 1359、更换进程、运行满 15 分钟或日志达到 64 MiB 时自动结束。
手动停止与添加时间标记：

```lua
kbAfkProbe.mark('manual-input-start')
kbAfkProbe.mark('manual-input-finished')
kbAfkProbe.stop('manual')
```

分析时同时保留 `KeyboundProbe.auto.log`、ACT 的 `FFXIVLogs\Network_*.log` 和同局 `.memory.tsv`。
CE 日志的 `utc` 是 UTC，ACT 自动化日志使用本机时间；国服本机为 UTC+8 时需转换后对齐。
`delta_ms` 为采集开始后的单调毫秒数。用户回复后补记的标记只能证明输入发生在标记之前，不能当作实际按键时间。

日志包括：

- `TIMER`：通用输入计时器和时间上限，每次采样都记录。
- `BASELINE` / `CHANGE`：按四字节记录初值和变化，保留十六进制、无符号整数和 float 三种解释。
- `KeyboundDirector-base`：从 EventFramework 的当前 ContentDirector 获取，要求 InstanceContentType 为 23；读取基础结构 0x1F98 字节。
- `KeyboundDirector-extra-unverified`：基础结构后的候选区域，范围 0x2068 字节；子类边界尚未确认，可能包含相邻分配，不能把这些字段直接命名为 AFK 状态。
- `KTGInput`：输入 Addon 的头部状态，不记录聊天文字。
- `STOP`：采集结束原因。

`ContentDirector + 0xCF0` 是任务剩余时间，不是闲置倒计时。通用输入计时器为 0、答案提交成功以及 `PHYSICAL_KEYS_OK` 均不能单独证明 AFK 已重置。

第一份 `keybound-20261103-142454-55196343.ce-afk.tsv` 共 419 个样本，已在强退后自动结束。
这份历史日志每行末尾有一列额外的替换计数（通常为 0），分析时忽略即可；脚本已修正后续日志格式，原始日志保持不变。
