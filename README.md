# Plain Craft Launcher (PCL) 下载恢复工具（第三方扩展）

这是给 **Windows 官方 PCL 本地自定义主页**使用的下载中心，针对 PCL 2.99 正式版移除内置 Minecraft 下载入口后的情况。主页上的按钮会启动一个**独立的 Windows 桌面程序（单文件 exe）**，不注入、不替换、不修改 PCL 程序文件。

## 文件说明

| 文件 | 用途 |
|---|---|
| `PCLDownloadRestore-Setup.exe` | **安装程序**（单文件，安装内容已内嵌，双击即用） |
| `PCLDownloadRestore.exe` | 下载中心主程序（.NET Framework WinForms，绿色版，无需安装） |
| `Custom.xaml` | PCL 自定义主页面板，按钮指向上面的 exe |
| `install.cmd` / `install.ps1` | 备用安装方式（不解压 Setup 时使用） |
| `src/PCLDownloadRestore.cs` | 下载中心完整 C# 源码（MIT） |
| `src/Setup.cs` | 安装程序完整 C# 源码（MIT） |
| `PCLDownloadFix.ps1` | 旧版 PowerShell 实现，保留作备用 |

> 非 PCL 官方插件或官方背书。PCL 2.99.0.0 发布说明因遵守相关法律法规暂时移除 MC 下载功能；对应实现还启用了 `RestrictMcDownload`，并限制整合包安装：<https://github.com/Meloong-Git/PCL/releases/tag/2.99.0.0> · <https://github.com/Meloong-Git/PCL/commit/e4b0489a22cfd4bc57a18fc1e2ac66d5c18e7fc1.patch>。PCL 自定义主页事件文档：<https://github.com/Meloong-Git/PCL/wiki/%E8%87%AA%E5%AE%9A%E4%B9%89%E4%BA%8B%E4%BB%B6>。

## 功能

- **原版 Minecraft**：读取 Mojang 公开版本清单，下载指定版本 JSON、客户端 JAR、适用于 Windows 的库/原生库、日志配置，以及可选的 Assets。对有元数据校验值的文件验证 SHA-1 和文件大小；官方源失败时按 PCL 上游公开的域名映射尝试 BMCLAPI，并有限重试。**依赖与资源按 4 线程并发下载**，支持 **HTTP Range 断点续传**（中断后保留 `.part`，下次从断点继续），并自动记住上次选用的游戏目录、保存目录与代理（`PCLDownloadRestore.ini`）。
- **Modrinth**：搜索整合包、Mod、资源包和光影包；可按游戏版本及加载器筛选；下载 Modrinth 发布文件并验证 SHA-512。
- **整合包安装**：在 Modrinth 页签点「安装 .mrpack 整合包」，**先弹出预览**（名称 / Minecraft 版本 / 加载器 / 文件数 / 总大小），确认后才开始。安装流程：下载对应原版文件 → **Fabric / Quilt** 读取官方 meta 下载加载器库，**Forge / NeoForge** 调用官方安装器（需本机有 Java）→ 下载整合包内所有文件（SHA-512 校验）→ 复制 `overrides/` → 生成 `versions/<实例名>/<实例名>.json`。完成后自动打开实例目录，PCL 刷新版本列表即可看到。
- **代理**：底部可填 `http://127.0.0.1:7890` 之类的 HTTP 代理，对所有请求生效，留空即直连。
- **加载器下载安装**（「加载器」页签）：选 Forge / NeoForge / Fabric / OptiFine 和 Minecraft 版本，列出该版本下全部可用加载器版本并一键安装。
  - Forge：读取 `maven.minecraftforge.net` 官方清单（5000+ 版本），下载安装器并调用 Java 静默安装。
  - NeoForge：读取 `maven.neoforged.net` 清单，自动把 `20.1.0` 映射到 `1.20.1`、`26.3.0.95` 映射到 `26.3`。
  - Fabric：读取官方 meta，直接生成实例，不需要安装器。
  - OptiFine：读取 BMCLAPI 清单，下载官方安装器（安装器需要图形界面，需手动双击）。
- **直接下载**：对用户明确输入的 HTTP/HTTPS 链接下载文件。HTTP 会显示风险提示；已存在的文件不会被静默覆盖。
- 不收集账号凭据、不请求 Microsoft/Mojang 令牌、不启动游戏、不运行下载的文件。

## 安装（推荐一键）

1. 下载并运行 **`PCLDownloadRestore-Setup.exe`**（单文件安装程序，无需解压）。
2. 点「选择 PCL.exe 并安装」，选中你的 **PCL.exe** → 安装程序会自动：
   - 把 `PCLDownloadRestore.exe` 释放到 PCL.exe 同目录（已有同名文件先备份为 `.bak-时间戳`）
   - 把 `Custom.xaml` 释放到 `PCL\Custom.xaml`（同样先备份）
3. 重启 PCL → **设置 → 个性化 → 主页**（在页面靠下位置）→ 选择 **本地文件** → 点 **刷新主页**。
4. 主页出现「PCL 下载恢复工具」卡片，点 **打开下载中心**。

### 备用安装方式

如果你不想运行安装程序，也可以解压后双击 `install.cmd`，效果相同（但需要系统 PowerShell）。

### 手动安装

1. 关闭 PCL。
2. `PCLDownloadRestore.exe` → 放进 **PCL.exe 所在文件夹**（`{path}` 指向的目录）。
3. `Custom.xaml` → 放进 `PCL 目录下的 PCL\Custom.xaml`（PCL 只从这个固定路径读取本地主页）。
4. 重启 PCL，设置 → 个性化 → 主页 → 本地文件 → 刷新主页。

> 如果找不到「主页」设置：它在 **设置 → 个性化** 页面最下面一张卡片，标题就是「主页」，里面有「生成教学文件」「刷新主页」按钮。旧版本叫「自定义主页」。

## 使用

- 点主页的“打开下载中心”会启动 `PCLDownloadRestore.exe`。它不修改系统执行策略、不需要管理员权限。
- 原版下载先确认“Minecraft 游戏目录”。应选择 PCL 当前实例实际使用的游戏根目录；默认值 `%APPDATA%\.minecraft` 不一定是你的 PCL 目录。下载后在 PCL 刷新版本列表。
- 整合包安装：**原版、Fabric、Quilt** 由本工具直接完成；**Forge / NeoForge** 会下载并调用官方安装器（`--installClient`），需要本机已安装 **Java（建议 17+）** 且能联网拉取库。个别旧版安装器不支持该命令行参数，失败时请改用官方安装器手动安装。
- PCL 2.99 官方版关闭了自带整合包导入，本工具不修改 PCL，只是自己在 `versions/` 下生成实例；PCL 刷新版本列表后按普通版本对待。

## 重要限制

- 这是本地自定义主页 + 独立 exe 下载工具，不是 PCL 官方插件 API。PCL 2.99.0.0 源码启用了 `RestrictMcDownload`，会禁用内置 Minecraft 下载及整合包导入；此工具不修改 PCL、不注入代码，也不绕过该导入限制。
- 原版安装仅补齐 Mojang 版本元数据描述的客户端、库、Assets 等文件；不安装 Forge、Fabric、NeoForge，不处理启动参数，不启动游戏。旧版自定义资源映射及个别非标准版本可能需额外处理。
- BMCLAPI 镜像可用性不保证；本次核验观察到部分端点返回 403/404。工具优先尝试官方源，镜像只作尽力回退，不代表必然可用。
- Modrinth 支持项目检索与文件下载；**不提供 CurseForge API 搜索或绕过其访问限制**。可以尝试使用“直接下载”，但需要你拥有合法可访问的直链。
- Mod/资源包下载后不会自动覆盖或改写实例；请自行放入正确实例目录。网络故障可取消后重试；取消无法中断正在进行的单个 HTTP 请求，单请求超时上限为 120 秒。
- 下载来自 Mojang、BMCLAPI、Modrinth、Modrinth 文件 CDN 镜像 `mod.mcimirror.top` 或你自行输入的站点。镜像文件按 Mojang/Modrinth 元数据校验；校验失败会被拒绝。直接链接没有可信哈希时无法证明文件来源和安全性。不要运行不信任的 JAR/EXE。
- 正版账户及游戏授权仍由用户自行负责；本工具不绕过正版验证。

## 验证状态

本交付包含完整源码。已验证：

- `PCLDownloadRestore.exe` 使用 .NET Framework 4.8 的 `csc` 编译通过，PE 头与 GUI 子系统标识正常，程序集内含预期中文界面字符串；
- PowerShell 安装脚本通过 AST 语法解析；
- `Custom.xaml` 通过 XML 结构检查，已移除 PCL 不支持的属性（`IsWarn`、`IsSwaped`）；
- Assets / 库镜像 URL 映射及路径穿越防护已做静态测试。

当前环境没有安装的 PCL，且该环境的 GUI 进程启动受限，因此**尚未在实际 PCL 窗口、真实大文件下载和真实整合包安装上做端到端验证**。整合包安装的解析/下载/实例生成逻辑已编译通过，但 Fabric / Quilt 实例能否被 PCL 正确识别仍需实测。建议先选一个独立空目录、较小的整合包验证。

## PCL 来源与许可说明

本工具独立实现下载逻辑，只参考 PCL 已公开的自定义事件说明、下载源 URL 映射和许可指南，没有复制 PCL 源码。PCL 项目作者为龙腾猫跃；上游仓库：<https://github.com/Meloong-Git/PCL>；赞助链接：<https://meloong.com/afd/a/LTCat>；上游许可/合理使用指南：<https://github.com/Meloong-Git/PCL/blob/main/LICENCE>。

本项目是涉及 Minecraft 下载功能的第三方扩展，名称明确标注 PCL 第三方属性。**本仓库为公开仓库，完整源码（`src/PCLDownloadRestore.cs`、`src/Setup.cs`、`Custom.xaml`）已随 MIT 许可公开，可直接查看、修改与再分发；`v1.6.1` 压缩包只是便于安装的打包副本，不是唯一分发形式。** 公开分发时请继续逐项遵守 PCL 仓库当前的《合理使用指南》（署名、第三方命名、赞助劝导等），并注意 PCL 上游自身并非 MIT 许可。
