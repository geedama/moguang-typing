# 魔光打字通

适用于 Windows 呆萌 ACT 的魔光键影自动输入插件。插件直接读取游戏中的 `KTGEWord` / `KTGInput` 界面结构，逐只怪物输入中文题目、回车攻击，并等待输入框或题目状态确认后继续。

## 运行条件

- 已能正常记录日志的呆萌 ACT，包含 `FFXIV_ACT_Plugin`。
- 若要使用悬浮窗，需要呆萌整合中的 `OverlayPlugin/ngld`。
- ACT 与游戏必须使用相同权限等级；若游戏以管理员运行，ACT 也应以管理员运行。

游戏更新可能改变内存结构。若插件页一直显示找不到 `KTGEWord` 或 `KTGInput`，请停止自动攻击并等待兼容更新。

## 一键安装

1. 完全退出 ACT 和呆萌启动器，任务管理器中不要残留多个 ACT 实例。
2. 解压整个 ZIP，不能只把 DLL 单独拖出来。
3. 双击 `安装到呆萌ACT.cmd`。
4. 选择呆萌 ACT 根目录，也就是包含 `Advanced Combat Tracker.exe`、`DLibs`、`Config` 的目录。
5. 安装器会备份 ACT 和 OverlayPlugin 配置，然后复制插件、启用插件并注册名为“魔光打字通”的悬浮窗。
6. 启动 ACT。插件页出现 `KeyboundProbe.ActPlugin.dll` 且状态为已加载，即安装成功。

升级时重复以上步骤即可。已有的 `KeyboundProbe.settings.ini` 会保留，不会被默认设置覆盖。
如果 ACT 安装在受保护目录并提示“拒绝访问”，请右键以管理员身份运行安装脚本。

## 使用方法

1. 启动 ACT，确认 `FFXIV_ACT_Plugin：已连接`。
2. 启动并切回 FFXIV，游戏必须保持前台。
3. 进入魔光键影玩法。插件页应显示 `KTGEWord 1`，出现小怪后会列出中文题目。
4. 在插件页勾选“启用 ACT 直读自动攻击（逐只确认）”，或在 ngld 悬浮窗点击“自动攻击关闭”。
5. 插件一次只处理一只怪物：聚焦输入框、输入题目、回车提交，检测到输入框清空、题目切换或怪物死亡后才会继续。
6. 需要立即停止时，在插件页点击“立即停用”，或在悬浮窗关闭自动攻击。

默认配置会根据题目长度动态控制输入节奏：从开始输入到回车不少于约 1500 ms，最长不超过 4000 ms；这两个值均可在 ACT 插件页调整。为防止玩法把纯窗口消息判断为长时间无操作，插件会在安全间隙每隔约 25–55 秒执行一次物理按键的错误数字提交；这可能产生少量错误惩罚，但不会拼接到正确答案中。

## 悬浮窗

安装器会自动添加“魔光打字通”，默认尺寸为 `520 × 310`。如果没有出现：

1. 打开 ACT 的 `插件 → OverlayPlugin悬浮窗插件`。
2. 检查列表中是否有“魔光打字通”，确保可见且未禁用。
3. 若自动注册失败，新建 MiniParse/自定义悬浮窗，将 URL 指向：

   `ACT根目录\Plugins\KeyboundProbe\overlay\index.html`

4. 重新加载悬浮窗。`runtime.js` 会在插件每次启动时自动生成，不应从其他电脑复制。

## 设置文件

路径：`Plugins\KeyboundProbe\KeyboundProbe.settings.ini`

| 设置 | 含义 |
| --- | --- |
| `Humanize` | 启用反应时间和逐字延迟的随机变化 |
| `BlatantMode` | 去除人为延迟；不建议日常开启 |
| `StartDelayMinMs/MaxMs` | 发现题目后的反应延迟 |
| `CharacterDelayMs` | 每字基础延迟 |
| `JitterPercent` | 每字延迟抖动百分比 |
| `SubmitDelayMinMs/MaxMs` | 输入完成到回车的延迟 |
| `TypingSubmitMinimumMs` | 从开始输入到回车的最短时间 |
| `TypingSubmitBudgetMs` | 从开始输入到回车的最长预算；不得小于最短时间 |
| `TargetOrder` | `slot_asc`、`slot_desc`、`shortest` 或 `longest` |

修改后可在插件页调整并保存。

## 故障排查

- **ACT 提示日志文件被占用**：关闭所有 ACT/呆萌实例，在任务管理器确认没有残留进程后再启动，只能运行一份同目录 ACT。
- **能看到题目但不输入**：保持 FFXIV 在前台，确认自动攻击已开启，并查看插件页的 `KTGInput` 诊断。
- **输入内容错误或乱打**：立即停用；确认使用的是与当前游戏版本匹配的插件，不要同时运行旧桥接或另一份自动输入工具。
- **悬浮窗离线**：先确认 ACT 插件已加载；检查 Windows 防火墙是否阻止 ACT 的本机 `127.0.0.1` 回环通信，然后刷新悬浮窗。
- **被强制退出任务**：查看 `KeyboundProbe.auto.log` 中是否周期性出现 `ACTIVITY_OK`。没有时通常是游戏不在前台或 `KTGInput` 一直处于占用状态。
- **需要反馈**：附上插件版本、游戏版本、问题发生时间，以及 `Plugins\KeyboundProbe\KeyboundProbe.auto.log` 对应时间附近的片段；不要公开完整抓包。

## 文件与隐私

- 自动化日志：`Plugins\KeyboundProbe\KeyboundProbe.auto.log`。
- 网络探针只有手动点击“开始捕获”才会写入 `Captures`；正常使用不需要捕获。
- ngld 控制服务只监听 `127.0.0.1`，每次 ACT 启动会生成新的随机 token，不访问互联网。
- 分发包不包含任何本机日志、抓包、角色数据、token、PDB 或旧版桥接文件。

## 卸载

关闭 ACT 后双击 `卸载.cmd`。卸载器会禁用悬浮窗、移除 ACT 插件注册和程序文件，但保留设置、日志与抓包，便于恢复或排查。

## 致谢

内存结构兼容性研究参考了 [SiGeoMa/ff14-keybound-brawler](https://github.com/SiGeoMa/ff14-keybound-brawler)。本分发包不包含该仓库的二进制或源码。

## 许可证

本软件可供个人免费下载、使用、阅读源码和修改；未经授权，不得重新打包、公开分发或商业使用。完整条款见发布包中的 `LICENSE`。
