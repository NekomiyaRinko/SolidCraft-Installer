# SolidCraft Installer (SCI)

Windows Minecraft 下载与加载器管理工具，使用 .NET 10、C# 14、WPF 和 MVVM。

## 功能

- Minecraft 客户端、依赖库和资源下载、摘要校验及继续下载。
- Forge、Fabric、NeoForge 的安装、删除及先删除后安装的加载器更换。
- 每个实例的模组与存档独立，资源和依赖库共享。
- 任务进度、日志、取消及加载器兼容性警告。
- Java 路径、默认内存与游戏目录设置。
- BMCLAPI 优先、失败回退官方，可在设置中切换为官方源。
- 选择加载器后自动获取对应 MC 的加载器版本，MC 与加载器均支持全部、正式版和测试版筛选。
- 自动扫描标准 `versions/<版本>/<版本>.json`；非 SCI 创建的版本只读显示，不允许删除或修改加载器。

不包含游戏启动、账号登录或模组文件下载。默认不提供 Quilt。

## 运行

需要 Windows 和 .NET 10 SDK。Forge、NeoForge 安装需要兼容的 Java。

```powershell
dotnet run --project src/McDownload/McDownload.csproj
```

## NuGet

- `CommunityToolkit.Mvvm`：`8.4.0`。

界面使用 WPF 原生控件，无额外 UI 控件包。设置位于 `%LOCALAPPDATA%\SCI\settings.json`。

默认游戏目录为 `SCI.exe` 同目录的 `.minecraft`，不依赖启动时的工作目录。旧版自动使用的 `%LOCALAPPDATA%\SCI\Minecraft` 会改用新默认路径，不迁移文件；手动指定的其他目录继续保留，也可在设置中重新选择旧目录。保存“使用程序目录下的 .minecraft”后，移动程序仍使用新位置的 `.minecraft`。

正式版筛选依据：Minecraft 的 `release` 类型、Fabric 的 `stable` 字段，以及 Forge/NeoForge 的预发布后缀（alpha/beta/snapshot/pre/rc）。Forge 无预发布标记的构建归为正式版，不限于 recommended 构建。只读版本不假设其模组与存档已隔离；缺少父版本或 JSON 损坏会单独显示警告，不阻止其他版本出现。

BMCLAPI 覆盖游戏清单、版本 JSON、客户端、资源、依赖库、Fabric 元数据及 Forge/NeoForge Maven 文件。超时、HTTP 失败或已有摘要校验失败会回退官方；取消不会触发回退。Forge/NeoForge 的 Java 安装器进程内部联网由安装器决定，不保证使用镜像。

镜像节点返回 403 时，对应官方来源在 5 分钟内直接走官方，其余来源继续使用镜像；不反复请求故障节点。日志显示实际失败的节点地址（不包含签名查询参数）。手动取消会记录取消请求，网络超时不会显示为手动取消。

诊断日志默认位于 `%LOCALAPPDATA%\SCI\Logs`，可在设置中查看当前文件并打开文件夹。每次启动生成独立文件，单个文件达到 8 MB 后分段；记录请求来源、HTTP 状态、实际节点、耗时、回退结果和完整异常链（含 SSL/证书内层异常）。URL 查询参数、令牌、密码及常见用户目录会在写入前脱敏。日志仅保存在本机，不会自动上传。

## 验证

```powershell
dotnet run --project tests/SCI.Smoke/SCI.Smoke.csproj
```

检查页面切换、WPF 绑定、任务取消、镜像回退及校验、版本筛选、标准版本只读扫描与跨实例保护。验证使用临时实例和模拟 HTTP 文件，不下载真实游戏、不运行 Java、不回收真实实例；运行时会短暂显示窗口。

联网元数据检查（只获取版本列表）：

```powershell
dotnet run --project tests/SCI.Smoke/SCI.Smoke.csproj -- --online
```

Forge 1.21.1 / 52.1.20 的 403 回退联网检查（只读取安装器校验文件，不下载或运行安装器）：

```powershell
dotnet run --project tests/SCI.Smoke/SCI.Smoke.csproj -- --forge-network
```

## 打包（不含 .NET Runtime）

```powershell
dotnet publish src/McDownload/McDownload.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish/win-x64-hmcl
Move-Item artifacts/publish/win-x64-hmcl/SCI.exe artifacts/publish/win-x64-hmcl/SCI_HMCL.exe
```

输出为 `artifacts/publish/win-x64-hmcl/SCI_HMCL.exe`，不包含 .NET Runtime；运行需要安装 .NET 10 Windows Desktop Runtime（x64）。许可证与来源文件一并放在同一目录。

## License

SCI is distributed under GNU GPL version 3 or later. The download-source and fetch components are adapted from [HMCL](https://github.com/HMCL-dev/HMCL/tree/acc9740fa9289ded38553b6a35045fd608c1ea99), Copyright (C) 2020-2026 huangyuhui and HMCL contributors. The HMCL additional terms are retained in `HMCL-ADDITIONAL-TERMS.md`; source attribution is in `NOTICE`.

SCI is a modified, independent product—not an official HMCL release. Before distributing an executable, provide recipients the complete corresponding source and build/install materials as required by GPLv3. `LICENSE`, `NOTICE`, and `HMCL-ADDITIONAL-TERMS.md` are included beside published output.
