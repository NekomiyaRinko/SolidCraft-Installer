# SolidCraft Installer (SCI)

SCI 是 Windows 上的 Minecraft 下载与实例管理工具，支持原版客户端、Forge / Fabric / NeoForge 加载器，以及 Modrinth / CurseForge 整合包导入。

[下载发布版](https://github.com/NekomiyaRinko/SolidCraft-Installer/releases) · [快速开始](#快速开始) · [整合包导入](#整合包导入) · [开发与验证](#开发与验证) · [打包](#打包) · [许可证与声明](#许可证与声明)

## 主要功能

- 下载客户端、依赖库和资源，校验文件摘要并继续未完成的下载。
- 安装、移除和更换加载器，保留实例的模组与存档。
- 为 SCI 实例独立保存模组与存档，共享资源和依赖库。
- 从 Modrinth、CurseForge 分发包创建整合包实例，支持整窗拖入与未完成导入续作。
- 自动发现已有 Minecraft 版本，集中查看实例信息。
- 可选的 BMCLAPI 镜像加速。

## 快速开始

### 运行要求

- **系统**：Windows x64。
- **运行时**：.NET 10 Windows Desktop Runtime（x64）。
- **Java**：安装 Forge、NeoForge 时需要与目标版本兼容的 Java；可在设置中指定路径，或留空由 SCI 查找。

程序和对应源码 ZIP 见 [GitHub Releases](https://github.com/NekomiyaRinko/SolidCraft-Installer/releases)。Git 仓库保存源码、测试、文档与构建脚本，构建产物通过 Releases 分发。

### 创建与管理实例

1. 在“设置”中选择游戏目录、下载源和 Java 路径。设置校验通过后自动保存。
2. 打开“下载游戏”，选择 Minecraft 和加载器版本，点击“下载并安装”。空白实例名自动命名为 `Minecraft版本-加载器名-加载器版本`；手动输入的名称优先。
3. 点击侧栏底部的“任务队列”，查看正在执行和等待执行的任务。返回原页面后任务继续运行。
4. 在“我的实例”中选择实例，查看详情、管理加载器或打开文件夹。

- **更换加载器**：先移除旧加载器，再安装目标版本。模组文件保留，兼容性由目标加载器和模组版本决定。
- **删除实例**：确认后将整个实例目录移至回收站，包括模组、存档和配置。
- **已有版本**：按 `versions/<版本>/<版本>.json` 扫描。非 SCI 创建的版本仅支持查看信息和打开文件夹。父版本缺失或 JSON 损坏会在对应实例中显示警告，其余版本仍可浏览。

### 当前范围

SCI 专注于下载安装与实例管理。游戏启动、账号登录、通用模组搜索与管理和其他模组加载器安装不在当前功能范围内。

## 整合包导入

### 导入与续作

1. 在“下载游戏”页选择“导入整合包”，点击“选择文件”；也可从任意页面将一个 Modrinth `.mrpack` 或 CurseForge `.zip` 分发包拖入窗口。
2. 确认包信息、实例名称、安装器 Java 和可选文件。可选文件默认不安装。
3. 加入下载队列。页面随即重置，可继续添加下一个包；进度在侧栏“任务队列”查看。删除确认等模态操作期间不接受拖入。
4. 失败、取消或文件待补齐时，在“我的实例”的实例详情中选择“继续导入 / 补齐文件”。源包移动后可重新选择内容相同的分发包。

导入创建新 SCI 实例，按清单固定 Minecraft 和加载器版本，安装配置、模组、资源包等文件。当前不支持覆盖已有实例或整合包更新。

续作保留实例和已完成文件，已写入文件被修改或删除时报告冲突，不覆盖用户配置。具备完整路径和摘要的受限文件可打开下载页面、选择本地文件并校验；缺少 API 元数据时不能仅凭文件名补齐。当前支持文件级续作，不支持单文件 HTTP 分段断点下载。

### CurseForge API key

解析 CurseForge 清单引用的远程文件前，需在设置页配置**应用 API key**。输入框下提供 [CurseForge 开发者控制台](https://console.curseforge.com/) 入口，经确认后打开默认浏览器。API key 不是账号密码，SCI 未内置共用凭据；读取 ZIP 内的整合包基本信息不需要凭据。

- **仅本次运行**：输入密钥并点击“应用”，默认不保存到磁盘。
- **在此电脑记住**：勾选后点击“应用”，以 Windows DPAPI `CurrentUser` 加密保存至 `%LOCALAPPDATA%\SCI\curseforge-key.dpapi`，下次启动自动读取。
- **切回临时使用**：取消勾选，重新输入密钥并应用。
- **清除**：移除当前凭据和本地保存项。任务执行期间不能修改凭据。

普通设置、实例记录和诊断日志不保存密钥；日志导出不包含凭据文件，源码打包拒绝 `.dpapi` 文件。DPAPI 保护磁盘上的密文，不能防止以当前用户身份运行的恶意程序读取密钥。密文迁移到其他电脑或 Windows 用户后可能无法解密；解密失败需重新输入，不回退为明文。

加入队列前，界面检查首个选中文件的 API 访问能力；队列执行时，在创建实例、下载游戏前解析全部选中文件的元数据。凭据缺失、认证拒绝、请求失败或元数据不可用会提前报错。具备完整元数据但限制自动下载的文件进入待补齐流程，网页链接取自 API 返回的可信项目地址。旧版待补齐实例可配置有效密钥后使用原分发包继续，无需删除实例。

### 文件校验与保护

- 下载验证清单提供的大小和摘要，Modrinth 同时验证 SHA-1、SHA-512。
- 客户端覆盖顺序为清单文件、`overrides`、`client-overrides`，同路径使用最高优先层内容，不安装 `server-overrides`。
- 下载地址及重定向限定为受支持的 HTTPS 站点；归档条目和目标路径检查边界、链接、重复路径、大小及受管文件保护。
- 导入不执行包内脚本；加载器安装沿用 SCI 现有安装逻辑。

## 任务队列

实例下载和加载器安装按提交顺序串行执行，下载页可在任务运行期间继续添加安装任务。每项任务固定提交时的实例名、Minecraft 版本、加载器版本和 Java 路径，后续修改表单只影响新提交的任务。

单项任务内，游戏资源和依赖库先并行校验缓存，再并行下载缺失或损坏的文件；相同目标文件只处理一次。在“设置 → 下载源 → 最大线程数”调整并发上限，范围为 1–64，默认 8，校验和下载均使用此上限。设置自动保存，队列运行期间不可修改，不改变任务队列的串行顺序。

- **执行中**：彩色卡片显示整体进度、各阶段进度、完成情况及尚未开始的阶段，可展开该任务的日志。
- **等待执行**：灰色卡片显示名称和等待状态，可移出队列；执行前不显示详细进度。
- **完成、取消或失败**：任务收起并保留结果；失败项保留错误信息，最近 50 项结束记录可展开查看。

整体百分比按阶段进度汇总，客户端按下载字节、依赖库和资源按完成项数更新。进度无法计算时显示“进行中”。取消或失败的任务完成清理后，队列继续下一项。关闭程序会取消当前项并移出等待项，清理完成后退出；队列在本次运行期间保存。

删除实例、移除或更换加载器、切换游戏目录和保存设置需等待队列空闲。

## 设置与下载源

### 游戏目录

默认游戏目录是程序所在目录下的 `.minecraft`，与启动时的工作目录无关。默认目录按便携模式保存，移动程序后会使用新位置的 `.minecraft`；自定义的非默认目录保持不变。切换目录只改变访问位置，不迁移文件。

### 设置保存

普通设置的文本输入在失去焦点后校验并保存，下拉选择或浏览路径后立即保存。无效值保留已保存的配置，页面显示校验或保存结果。CurseForge 凭据需单独点击“应用”。关闭程序时，SCI 会提交当前普通设置输入并等待保存或正在进行的凭据写入完成。

### 版本筛选与下载源

Minecraft 和加载器均提供“全部”“正式版”“测试版”筛选。Minecraft 使用 `release` 类型，Fabric 使用 `stable` 字段，Forge、NeoForge 根据 alpha、beta、snapshot、pre、rc 等预发布标记分类。Forge 正式版包含无预发布标记的构建，范围大于 recommended 构建。

BMCLAPI 加速覆盖游戏清单、版本 JSON、客户端、资源、依赖库、Fabric 元数据及 Forge、NeoForge Maven 文件。超时、HTTP 请求失败或摘要校验失败时，SCI 回退官方源；主动取消会停止任务。Forge、NeoForge 的 Java 安装器自行处理其进程内部的联网请求。

## 诊断日志

日志位于 `%LOCALAPPDATA%\SCI\Logs`，可从设置页打开。每次启动生成独立日志，单个文件达到 8 MB 后分段，记录请求来源、HTTP 状态、实际节点、耗时、回退结果和异常链，包括 SSL、证书错误；异常链最多展开 32 层，长文本截断。

写入前移除 HTTP(S) URL 查询参数，并对已识别的常见凭据字段和当前用户目录脱敏。日志保存在本机，分享前请检查内容。

设置页提供“导出日志”和“删除日志”。导出会将 SCI 生成的日志保存为本地 ZIP；删除需在居中弹窗中确认，确认后日志将移至回收站，并重新开始记录当前日志。任务记录、游戏文件和其他文件保持不变。部分文件无法回收时会显示清理未完成，后续日志继续记录。

## 开发与验证

项目使用 **.NET 10 SDK、C# 14、WPF 和 MVVM**，NuGet 依赖为 `CommunityToolkit.Mvvm 8.4.0`，界面使用 WPF 原生控件。

### 构建与运行

在 Windows 上，从仓库根目录执行：

```powershell
dotnet build SCI.slnx
dotnet run --project src/McDownload/McDownload.csproj
```

### 界面文本

界面文本位于 [`src/McDownload/i18n/`](src/McDownload/i18n/)，使用 .NET 原生 `.resx` 资源，按页面和服务分组，`Common.resx` 存放公共文案。XAML 通过 `{i18n:Loc Group, Key}` 引用；C# 通过 `Text.Get("Group", "Key")` 或 `Text.Format("Group", "Key", args)` 引用。带变量的文案以完整模板保存，格式化使用当前区域设置，缺失资源直接报错。

资源覆盖静态标签、弹窗、文件选择框、状态和界面可见的后端错误；导航标识、协议字段、路径和持久化数据值不作翻译；第三方动态内容及完整许可原文保持原来源。修改文案时优先只改对应 `.resx` 的 `value`，保留语义资源键、格式占位符、换行及筛选器语法。离线回归校验编译资源、缺键、区域回退、源码引用和硬编码遗漏，并保留现有 UI 行为与文案断言。

### 整合包接口

- **预览与导入**：`WorkspaceViewModel.Modpacks.PreviewAsync` 返回包信息、客户端文件、可选文件键和源包 SHA-256；`ImportAsync` 创建实例，`ResumeAsync` 根据实例 ID 继续，`GetStateAsync` 读取结果。通过 `SelectedOptionalFiles` 选择可选文件。
- **队列**：`WorkspaceViewModel.EnqueueModpackAsync` / `ResumeModpackAsync` 接入串行队列；新导入须提供预览所得的 `ExpectedArchiveSha256`。待补齐结果标记为未完成，不显示安装成功。
- **凭据与解析**：CurseForge 文件使用官方 API 解析。`WorkspaceViewModel.SetCurseForgeApiKey` 配置当前凭据，同时保留 `curseForgeApiKey` 构造回调及 `CurseForgeFileResolver` 注入接口。凭据缺失或认证失败抛出预检查错误，元数据完整但受限的文件返回 `NeedsFiles`。
- **本地补齐**：`ModpackResumeRequest.LocalFiles` 将文件键映射到完整本地路径，补齐仍须通过摘要校验。
- **状态记录**：实例内的 `.launcher/modpack/state.json` 保存导入结果和缺失项，供实例详情与续作页面读取。

### 回归测试

运行离线回归：

```powershell
dotnet run --project tests/SCI.Smoke/SCI.Smoke.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File tests/SCI.Packaging/PublishChecks.ps1
```

添加 `-- --fixture-only` 可跳过 `modpack_test` 目录中的可选真实整合包样本界面检查，其余离线模拟回归保持运行。

离线回归使用隔离数据和模拟请求，运行时会短暂显示窗口，不下载真实游戏、执行 Java 或回收真实实例。覆盖范围包括：

- **界面与设置**：导航、WPF 绑定、对齐、颜色、下拉交互、设置自动保存、空状态、确认弹窗及文案资源。
- **实例与安装**：版本筛选、已有版本只读访问、跨实例保护、独立版本 JSON、安装与运行依赖隔离、处理器生成文件保留。
- **队列与下载**：排队、取消、进度隔离、提交参数快照、镜像回退和文件校验。
- **整合包**：格式识别、可选文件、覆盖层、危险路径、摘要失败、CurseForge API 与本地补齐、Fabric 和旧版 Forge 安装链路、取消续作、中断恢复、源包移动及用户配置冲突。
- **拖放与日志**：各页面和文本框拖放、读取竞争、提交后重置、重名保护；日志导出、回收确认、文件范围及清理后继续记录。界面测试生成正常和最小窗口尺寸截图。

联网元数据检查只获取版本列表：

```powershell
dotnet run --project tests/SCI.Smoke/SCI.Smoke.csproj -- --online
```

## 打包

在 Windows 上从待分发版本的源码运行打包脚本，生成 Windows x64 单文件程序及对应源码 ZIP，使用外部 .NET Runtime：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

执行策略参数仅作用于此次打包进程。输出目录固定为 `artifacts/publish/sci-hmcl/`，重复运行会覆盖旧包，运行前请关闭该目录中的 SCI 并备份需保留的旧版本。整包先在暂存目录生成，再替换包内文件，替换失败时恢复旧文件；用户数据不参与替换。脚本阻止同时打包，并在构建前后检查 `.minecraft` 等用户文件，发现后停止覆盖。目录包含：

- `SolidCraftInstaller.exe`：依赖 .NET 10 Windows Desktop Runtime（x64），不捆绑运行时。
- `SolidCraftInstaller-source.zip`：本次构建的项目、测试、打包脚本、README 和许可证；排除 `bin/obj`、日志及用户错误报告。
- `LICENSE`、`NOTICE`、`HMCL-ADDITIONAL-TERMS.md`、`THIRD-PARTY-NOTICES.md`：许可证、来源及第三方声明。

分发时保留这些文件，并提供与二进制对应的完整源码及构建材料。

## 许可证与声明

SCI 按 **GNU GPL v3 或更高版本**发布，是基于 HMCL 组件修改的独立产品，非 HMCL 官方发行版。下载源管理、文件获取组件及游戏本体下载逻辑改编自 [HMCL](https://github.com/HMCL-dev/HMCL/tree/acc9740fa9289ded38553b6a35045fd608c1ea99)。

Copyright (C) 2020–2026 huangyuhui and HMCL contributors.

- [LICENSE](LICENSE)：GPL v3 完整文本。
- [HMCL-ADDITIONAL-TERMS.md](HMCL-ADDITIONAL-TERMS.md)：HMCL 附加条款。
- [NOTICE](NOTICE)：来源与修改声明。
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)：CommunityToolkit.Mvvm 8.4.0 的 MIT 版权、许可全文及随包第三方声明。

SCI 不托管、不分发游戏文件。Minecraft 文件及加载器由 SCI 从 Mojang、加载器官方源或第三方镜像源获取后写入用户本地实例目录，不随 SCI 发布包捆绑，各自由其权利方的使用条款管理。

SCI 使用 [BMCLAPI](https://bmclapi2.bangbang93.com) 作为可选的 Minecraft 资源下载镜像源。BMCLAPI 由 bangbang93 开发并维护。BMCLAPI 下的文件归源站点所有，SCI 不对其完整性、有效性或实时性作出保证。

SCI 的 GPL 声明不改变第三方组件各自的许可，由各自权利方的使用条款管理。
