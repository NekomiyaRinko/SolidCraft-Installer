# 回归测试索引

## 运行

```powershell
dotnet run --project tests/SCI.Smoke/SCI.Smoke.csproj -- --fixture-only
powershell -NoProfile -ExecutionPolicy Bypass -File tests/SCI.Packaging/PublishChecks.ps1
```

Smoke 按模块输出断言执行次数，最后汇总；`Program.cs` 另有跨模块的主窗口、实例保护、取消、许可分发和绑定检查。资源键、控件和输入组合循环均会累计断言数，不能把总数当作独立场景数。

`--fixture-only` 仅跳过 `ModpackSampleUiChecks` 的真实包界面检查。无论是否传入该参数，`ModpackChecks` 都会在 `modpack_test/` 存在时解析固定的 BMC2 和 RLCraft 样本；目录不存在时跳过这两项解析。样本不随仓库提供，已有目录须包含预期文件。`--online` 与 `--forge-network` 是独立联网入口，不属于离线验收。

## 模块归属

- **界面与反馈**：`UiChecks`（导航、布局、拖放）、`DownloadFeedbackChecks`（元数据加载与页面反馈）、`ColorStateChecks`（控件颜色状态）。
- **队列与并发**：`QueueChecks`（串行调度、参数快照、提交通知完整生命周期、停止队列）、`DownloadConcurrencyChecks`（下载并发限制）。
- **设置与日志**：`SettingsStyleChecks`（自动保存、输入提交、样式交互及日志弹窗）、`DiagnosticChecks`（诊断记录、导出和清理）。
- **安装与下载源**：`FeatureChecks`（元数据、实例隔离、游戏和加载器安装）、`ForbiddenChecks`（403/429 镜像回退）。
- **整合包**：`ModpackChecks`（解析、导入、摘要、安全及兼容性）、`ModpackQueueChecks`（排队快照和预检查）、`ModpackRecoveryChecks`（恢复）、`ModpackTransportChecks`（传输）、`ModpackUiChecks`（界面与拖放）、`ModpackSampleUiChecks`（可选真实包）。
- **凭据、资源与许可**：`CurseForgeCredentialChecks`（凭据配置）、`LocalizationChecks`（资源完整性及显示名行为）、`LicenseChecks`（关于页及许可导航）。
- **打包**：`SCI.Packaging/PublishChecks.ps1`（模拟发布、回滚、旧包升级清理和用户文件保护）。

## 保留与合并依据

- 没有整组测试因功能废弃而退出入口；旧目录迁移、旧格式状态和旧版 Forge 对应仍在使用的兼容分支，继续保留。
- 提交通知的出现与消退统一由 `QueueChecks` 验证；`DownloadFeedbackChecks` 专注加载反馈，不再检查未由自身触发的通知。
- 关于页版权和许可入口统一由 `LicenseChecks` 验证，包括实际导航和完整内容；主入口继续检查许可分发文件与嵌入内容的一致性。
- 关于页版本预期来自应用程序集及资源格式，避免随每次发布手动修改固定版本断言。
- `LocalizationChecks` 的源码扫描与资源替换行为检查共同保留；加载器安装、已安装版本识别与预览属于不同显示路径，预览检查不替代全部源码防护。

## 当前覆盖边界

- `ModpackQueueChecks` 的缺密钥用例覆盖预检查失败后接受新任务；真实 `NeedsFiles` 的后端行为在 `ModpackChecks` 中验证，尚未覆盖其队列包装及后续排队任务继续执行的组合路径。
- `SettingsStyleChecks` 直接验证 `CommitSettingsInput` 与自动保存；不覆盖 `App.OnStartup` 注册的实际窗口关闭处理器。
