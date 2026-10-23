# SolidCraft Installer (SCI)

SCI 是 Windows 上的 Minecraft 下载与实例管理工具，支持原版客户端和 Forge、Fabric、NeoForge 加载器。

- 下载客户端、依赖库和资源，校验文件摘要并继续未完成的下载。
- 安装、移除和更换加载器，保留实例的模组与存档。
- 为 SCI 实例独立保存模组与存档，共享资源和依赖库。
- 从 Modrinth、CurseForge 分发包创建整合包实例，支持整窗拖入与未完成导入续作。
- 自动发现已有 Minecraft 版本，集中查看实例信息。
- 可选的 BMCLAPI 镜像加速。

## 使用

运行发布版需要 **Windows x64** 和 **.NET 10 Windows Desktop Runtime（x64）**。Forge、NeoForge 安装还需要与目标版本兼容的 Java，可在设置中指定路径，或留空由 SCI 查找。

程序和对应源码 ZIP 见 [GitHub Releases](https://github.com/NekomiyaRinko/SolidCraft-Installer/releases)。下载程序及随附的许可、来源声明，并放在同一目录。Git 仓库保留源码、测试、文档与构建脚本，构建产物通过 Releases 分发。

1. 在“设置”中选择游戏目录、下载源和 Java 路径。设置校验通过后自动保存。
2. 打开“下载游戏”，选择 Minecraft 和加载器版本，点击“下载并安装”。空白实例名自动生成为 `Minecraft版本-加载器名-加载器版本`，原版使用 Minecraft 版本号；手动输入的名称优先。
3. 点击侧栏底部的“任务队列”，查看正在执行和等待执行的任务。返回原页面后任务继续运行。
4. 在“我的实例”中选择实例，查看详情、管理加载器或打开文件夹。

更换加载器会先移除旧加载器，再安装目标版本。模组文件保留，兼容性由目标加载器和模组版本决定。删除实例需在居中弹窗中确认，随后整个实例目录移至回收站，包括模组、存档和配置。

已有版本按 `versions/<版本>/<版本>.json` 扫描。非 SCI 创建的版本支持查看信息和打开文件夹，保持只读；其模组与存档沿用原有目录布局。父版本缺失或 JSON 损坏会在对应实例中显示警告，其余版本仍可浏览。

SCI 专注于下载安装与实例管理。游戏启动、账号登录、通用模组搜索与管理和 Quilt 安装不在当前功能范围内。

### 整合包导入

在“下载游戏”页选择“导入整合包”，点击“选择文件”，或从任意页面将一个 Modrinth `.mrpack` / CurseForge `.zip` 分发包拖入 SCI 窗口。拖入后自动切换到导入页；确认包信息、实例名称、安装器 Java 和可选文件后加入下载队列。提交后立即回到空白拖放首页，可继续添加下一个包，任务进度仍在侧栏“任务队列”查看。删除确认等模态操作期间不接受拖入。

导入只创建新 SCI 实例，按清单固定 Minecraft 和 Forge、Fabric 或 NeoForge 版本，安装包内配置、模组、资源包等文件；不支持 Quilt、已有实例覆盖或整合包更新。失败、取消或文件待补齐时，可在“我的实例”的实例详情中选择“继续导入 / 补齐文件”。源包移动后可重新选择；有完整元数据的受限文件支持打开下载页面、选择本地文件并验证。没有 API 元数据时不能凭文件名补齐。

解析 CurseForge 清单引用的远程文件前，请在设置页的 CurseForge 卡片中配置应用 API key。输入框下提供 [CurseForge 开发者控制台](https://console.curseforge.com/) 入口，点击后需确认才会打开默认浏览器；SCI 不随链接发送密钥。API key 不是账号密码，应用未内置可共用的凭据。读取 ZIP 内的整合包基本信息不需要凭据。

密钥默认仅本次运行有效。勾选“在此电脑记住 API key”并点击“应用”后，以 Windows DPAPI `CurrentUser` 加密写入 `%LOCALAPPDATA%\SCI\curseforge-key.dpapi`，下次启动自动读取；普通设置、实例记录和诊断日志不保存密钥。复制密文到另一台电脑或其他 Windows 用户下不保证可解密，解密失败时需重新输入，不回退为明文。DPAPI 保护静态文件，不能防止以当前用户身份运行的恶意程序读取密钥。点击“清除”会移除当前凭据和本地保存项。取消勾选后重新输入密钥并应用，可切回仅本次运行。任务执行期间不能修改凭据，日志导出不包含凭据文件，源码打包会拒绝 `.dpapi` 文件。

界面加入队列前检查首个选中文件的 API 访问能力；队列执行时，在创建实例、下载游戏前解析全部选中的 CurseForge 文件元数据。缺少凭据、认证拒绝、请求失败或元数据不可用时提前失败；认证错误与文件 404 分开提示。限制自动下载但具备完整路径和摘要的文件仍进入待补齐流程。旧版留下的待补齐实例可在配置有效密钥后使用原分发包继续，不需要删除实例。网页链接仅取自 API 返回的可信项目地址，不再使用数字项目 ID 拼接兜底链接；旧记录中的数字链接在界面中禁用。

- `WorkspaceViewModel.Modpacks.PreviewAsync` 返回包信息、客户端文件、可选文件键和源包 SHA-256。`ImportAsync` 创建实例，`ResumeAsync` 根据实例 ID 继续；`GetStateAsync` 读取结果。可选文件默认不安装，通过 `SelectedOptionalFiles` 选择。
- `WorkspaceViewModel.EnqueueModpackAsync` / `ResumeModpackAsync` 使用现有串行任务队列；前者要求提供预览所得的 `ExpectedArchiveSha256`。待补齐结果在队列中标记为未完成，不显示安装成功。实例详情与续作页面读取导入状态和缺失项。
- 文件下载验证清单提供的大小和摘要，Modrinth 同时验证 SHA-1、SHA-512。客户端覆盖顺序为清单文件、`overrides`、`client-overrides`，不安装 `server-overrides`。最终被覆盖的同路径文件直接使用最高优先层内容。
- CurseForge 文件解析使用官方 API。设置页调用 `WorkspaceViewModel.SetCurseForgeApiKey` 配置当前凭据，也保留 `WorkspaceViewModel` 的 `curseForgeApiKey` 构造回调及 `CurseForgeFileResolver` 注入接口。凭据缺失或认证失败抛出预检查错误；具备元数据但受限的文件返回 `NeedsFiles`。能够取得目标路径和摘要的受限文件可通过 `ModpackResumeRequest.LocalFiles`（文件键到完整本地路径的映射）补齐，仍须通过摘要校验；没有元数据时不能仅凭文件名补齐。
- 导入记录保存在实例 `.launcher/modpack/state.json`。失败或取消保留实例和已完成文件，续作需要内容相同的源分发包；源文件移动后可指定新路径。已写入文件被修改或删除时报告冲突，不覆盖用户配置。这里只支持文件级续作，不支持单文件 HTTP 分段断点下载。

下载地址及重定向限定为受支持的 HTTPS 站点；归档条目和目标路径经过边界、链接、重复路径、大小及受管文件保护检查。导入不执行包内脚本；加载器安装沿用 SCI 现有安装逻辑。整合包模组获取是本段后端接口的专用能力，未提供通用模组搜索或管理界面。

### 任务队列

实例下载和加载器安装按提交顺序串行执行，下载页可在任务运行期间继续添加安装任务。每项任务固定提交时的实例名、Minecraft 版本、加载器版本和 Java 路径，后续修改表单只影响新提交的任务。

- **执行中**：彩色卡片显示整体进度、各阶段进度、完成情况及尚未开始的阶段，可展开该任务的日志。
- **等待执行**：灰色卡片显示名称和等待状态，可移出队列；执行前不显示详细进度。
- **完成、取消或失败**：任务收起并保留结果；失败项保留错误信息，最近 50 项结束记录可展开查看。

整体百分比按阶段进度汇总，客户端按下载字节、依赖库和资源按完成项数更新。进度无法计算时显示“进行中”。取消或失败的任务完成清理后，队列继续下一项。关闭程序会取消当前项并移出等待项，清理完成后退出；队列在本次运行期间保存。

删除实例、移除或更换加载器、切换游戏目录和保存设置需等待队列空闲。

### 游戏目录与设置

默认游戏目录是程序所在目录下的 `.minecraft`，与启动时的工作目录无关。默认目录按便携模式保存，移动程序后会使用新位置的 `.minecraft`；自定义的非默认目录保持不变。切换目录只改变访问位置，不迁移文件。

普通设置的文本输入在失去焦点后校验并保存，下拉选择或浏览路径后立即保存。无效值保留已保存的配置，页面显示校验或保存结果。CurseForge 凭据需单独点击“应用”。关闭程序时，SCI 会提交当前普通设置输入并等待保存或正在进行的凭据写入完成。

### 版本筛选与下载源

Minecraft 和加载器均提供“全部”“正式版”“测试版”筛选。Minecraft 使用 `release` 类型，Fabric 使用 `stable` 字段，Forge、NeoForge 根据 alpha、beta、snapshot、pre、rc 等预发布标记分类。Forge 正式版包含无预发布标记的构建，范围大于 recommended 构建。

BMCLAPI 加速覆盖游戏清单、版本 JSON、客户端、资源、依赖库、Fabric 元数据及 Forge、NeoForge Maven 文件。超时、HTTP 请求失败或摘要校验失败时，SCI 回退官方源；主动取消会停止任务。Forge、NeoForge 的 Java 安装器自行处理其进程内部的联网请求。

### 诊断日志

日志位于 `%LOCALAPPDATA%\SCI\Logs`，可从设置页打开。每次启动生成独立日志，单个文件达到 8 MB 后分段，记录请求来源、HTTP 状态、实际节点、耗时、回退结果和异常链，包括 SSL、证书错误；异常链最多展开 32 层，长文本截断。

写入前移除 HTTP(S) URL 查询参数，并对已识别的常见凭据字段和当前用户目录脱敏。日志保存在本机，分享前请检查内容。

设置页提供“导出日志”和“删除日志”。导出会将 SCI 生成的日志保存为本地 ZIP；删除需在居中弹窗中确认，再将这些日志移至回收站，并重新开始当前日志。任务记录、游戏文件和其他文件保持不变。部分文件无法回收时会显示清理未完成，后续日志继续记录。

## 开发与验证

项目使用 **.NET 10 SDK、C# 14、WPF 和 MVVM**，NuGet 依赖为 `CommunityToolkit.Mvvm 8.4.0`，界面使用 WPF 原生控件。

```powershell
dotnet build SCI.slnx
dotnet run --project src/McDownload/McDownload.csproj
```

运行离线回归：

```powershell
dotnet run --project tests/SCI.Smoke/SCI.Smoke.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File tests/SCI.Packaging/PublishChecks.ps1
```

回归检查页面导航、WPF 绑定、控件对齐与颜色状态、下拉交互、设置自动保存、实例空状态、删除确认、任务排队与取消、进度隔离、排队参数快照、镜像回退、文件校验、版本筛选、已有版本只读访问及跨实例保护。安装测试覆盖独立版本 JSON、安装依赖与运行依赖隔离，以及处理器生成文件的保留。日志测试使用临时文件和模拟回收操作，检查导出、确认与取消、文件范围限制及清理后的继续记录。测试运行时短暂显示窗口，不下载真实游戏、执行 Java 或回收真实实例。

整合包回归覆盖格式识别、客户端/可选文件、覆盖层优先级、归档危险路径、摘要失败、CurseForge API 模拟与本地补齐、Fabric 和旧版 Forge 安装链路、取消续作、提交中断恢复、源包移动、队列参数快照及用户配置冲突保护。界面测试覆盖各页面和文本框上的 WPF 拖放事件、读取竞争与取消、提交后首页重置、重名保护、实例状态及本地补齐；同时生成正常与最小窗口尺寸的截图。若本地存在 `modpack_test` 下的 BMC2 与 RLCraft 样本，还会只读解析它们，不下载清单引用的真实文件。

CurseForge 修复回归另覆盖无凭据时不排队/不创建实例、HTTP 401/403/404/429 区分、可信类别页面和旧数字链接拦截、旧导入记录续作、真实 Windows DPAPI 加解密及篡改拒绝、仅内存模式与加密保存切换、启动读取、清除、任务期间凭据锁定和外部链接确认。测试仅使用隔离目录中的虚构密钥，不访问真实凭据或联网获取模组。

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

SCI 使用 [BMCLAPI](https://bmclapi2.bangbang93.com) 作为可选的 Minecraft 资源下载镜像源。BMCLAPI 由 bangbang93 开发并维护。BMCLAPI 下的文件归源站点所有，SCI不对其完整性、有效性或实时性作出保证。

SCI 的 GPL 声明不改变第三方组件各自的许可，由各自权利方的使用条款管理。
