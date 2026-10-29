# 魔光打字通

用于《最终幻想 XIV》金碟游乐场“魔光键影”的呆萌 ACT 插件。

插件读取游戏当前显示的中文题目，选择目标并完成输入。P1、P2 和 P3 的部分怪物机制也会参与目标排序和停手判断。

当前版本见 [VERSION](VERSION)。

## 安装

从仓库的 Releases 页面下载最新的 `MoguangTyping-版本号.zip`，完整解压后运行 `安装到呆萌ACT.cmd`。安装时需要关闭 ACT 和呆萌启动器。

安装器会要求选择呆萌 ACT 根目录，即包含 `Advanced Combat Tracker.exe`、`DLibs` 和 `Config` 的目录。安装完成后启动 ACT，在插件列表中确认 `KeyboundProbe.ActPlugin.dll` 已加载。

`KeyboundProbe` 是插件早期使用的内部名称。为兼容旧设置，DLL、插件目录和配置文件仍沿用这个名称。

本插件不依赖 Dalamud。悬浮窗需要呆萌整合中的 OverlayPlugin/ngld；不使用悬浮窗时不影响 ACT 插件本体工作。

## 使用

1. 确认 ACT 的 FFXIV 解析插件已经连接。
2. 启动 FFXIV，并保持游戏窗口在前台。
3. 进入魔光键影。
4. 在插件页或“魔光打字通”悬浮窗中开启自动攻击。

插件每次只处理一个目标。输入完成后会发送回车，并等待输入框清空、题目变化或怪物死亡，再选择下一个目标。

安装、设置和故障排查的完整说明在 [发布包 README](packaging/KeyboundProbe/README.md) 中。

## 目前支持的玩法规则

| 阶段 | 当前规则 |
| --- | --- |
| P1 | 先出现先攻击；累计 25 次有效死亡进入下一阶段 |
| P2 | 护盾 → 四次打断读条 → 奖励怪 → 导弹/自爆物 → 花苞/成长怪 → 普通目标 |
| P3 | 护盾 → 破坏加农炮 → 地雷 → 暴风破坏者 |

遇到诅咒视线或运动体探知干扰器时，插件会在读条结束前停手，并在读条结束后一秒恢复。视觉妨碍不会阻止题目读取。

贝希摩斯和陨石分支仍缺少足够的实战记录，目前只记录相关实体和读条，不使用未确认的 ID 调整优先级。

完整的优先级表和事件依据见 [攻击优先级与机制处理](docs/keybound-combat-priority.md)。

## 从源码构建

构建环境：Windows PowerShell、.NET Framework 4.x C# 编译器，以及本机 ACT 的 `Advanced Combat Tracker.dll`。

```powershell
./scripts/build-keybound-probe.ps1 -ActAssemblyPath 'D:\ACT\DLibs\Advanced Combat Tracker.dll'
./src/KeyboundProbe/artifacts/KeyboundProbe.Tests.exe
./scripts/package-keybound-probe.ps1
```

生成的 DLL、测试程序和发布包不会提交到 Git。

## 仓库结构

- `src/KeyboundProbe/`：ACT 插件、内存读取、输入、战斗状态机和测试。
- `src/KeyboundProbe/overlay/`：ngld 悬浮窗。
- `packaging/KeyboundProbe/`：安装器、卸载器和发布包模板。
- `scripts/`：构建、测试、部署和打包脚本。
- `docs/`：机制规则与发布流程。

## 版本与发布

- `VERSION` 是唯一产品版本来源。
- 版本号遵循语义化版本：`主版本.次版本.修订号`。
- 发布步骤见 [RELEASING.md](docs/RELEASING.md)。

## 数据与网络

- 正常运行产生的诊断日志保存在 ACT 插件目录，不进入源码仓库或发布包。
- 网络探针只在手动开始捕获后写入文件。
- 悬浮窗控制服务只监听本机 `127.0.0.1`，启动时生成临时访问令牌。

## 致谢

内存结构研究参考了 [SiGeoMa/ff14-keybound-brawler](https://github.com/SiGeoMa/ff14-keybound-brawler)。本仓库不包含该项目的二进制或源码。

## 许可证

本项目源码公开，允许免费下载、使用、阅读和个人修改；未经授权，不得重新打包、公开分发或商业倒卖。详见 [LICENSE](LICENSE) 和 [许可证 FAQ](docs/LICENSE-OPTIONS.md)。
