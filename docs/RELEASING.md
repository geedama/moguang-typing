# 发布流程

## 版本规则

`VERSION` 是唯一版本来源，格式为 `主版本.次版本.修订号`：

- 主版本：存在不兼容的安装、配置或协议变化。
- 次版本：新增兼容功能或重要玩法阶段。
- 修订号：兼容性修复、日志改进或文档修正。

## 发布前

1. 确认根目录存在已填写版权主体的 `LICENSE`；打包器会拒绝无许可证发布。
2. 根据本版本的提交和 PR 整理 Release Notes。
3. 更新 `VERSION`。
4. 关闭 ACT 和呆萌启动器。
5. 完整构建并运行核心测试。
6. 生成发布包并运行安装/升级/卸载集成测试。

```powershell
./scripts/build-keybound-probe.ps1
./src/KeyboundProbe/artifacts/KeyboundProbe.Tests.exe
./scripts/package-keybound-probe.ps1
./scripts/test-keybound-package.ps1 -PackageDirectory ./release/MoguangTyping-$(Get-Content ./VERSION)
```

## Git 标签与 GitHub Release

1. 确认工作区只包含预期源码和文档。
2. 提交：`release: prepare X.Y.Z`。
3. 创建带注释标签：`git tag -a vX.Y.Z -m "魔光打字通 X.Y.Z"`。
4. 推送提交和标签。
5. 创建 GitHub Release，标题使用 `魔光打字通 X.Y.Z`。
6. 上传 `MoguangTyping-X.Y.Z.zip` 与对应的 `.sha256` 文件。
7. 填写本版本的 Release Notes，并验证下载后的 SHA-256。

## 发布后

- 用干净的呆萌 ACT 目录执行一次安装与启动冒烟测试。
- 确认旧版设置可以保留升级。
- 确认发布包不含 `.log`、`.pdb`、抓包、`runtime.js`、角色信息或本机路径。
