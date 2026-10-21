# Plain Craft Launcher (PCL) 下载恢复工具（第三方扩展）

这是给 **Windows 官方 PCL 本地自定义主页**使用的下载中心，针对 PCL 2.99 正式版移除内置 Minecraft 下载入口后的情况。它通过 PCL 的自定义主页事件启动一个独立 PowerShell GUI，不注入、不替换、不修改 PCL 程序文件。

> 非 PCL 官方插件或官方背书。PCL 2.99.0.0 发布说明因遵守相关法律法规暂时移除 MC 下载功能；对应实现还启用了 `RestrictMcDownload`，并限制整合包安装：<https://github.com/Meloong-Git/PCL/releases/tag/2.99.0.0> · <https://github.com/Meloong-Git/PCL/commit/e4b0489a22cfd4bc57a18fc1e2ac66d5c18e7fc1.patch>。PCL 自定义主页事件文档：<https://github.com/Meloong-Git/PCL/wiki/%E8%87%AA%E5%AE%9A%E4%B9%89%E4%BA%8B%E4%BB%B6>。

## 功能

- **原版 Minecraft**：读取 Mojang 公开版本清单，下载指定版本 JSON、客户端 JAR、适用于 Windows 的库/原生库、日志配置，以及可选的 Assets。对有元数据校验值的文件验证 SHA-1 和文件大小；官方源失败时按 PCL 上游公开的域名映射尝试 BMCLAPI，并有限重试。
- **Modrinth**：搜索整合包、Mod、资源包和光影包；可按游戏版本及加载器筛选；下载 Modrinth 发布文件并验证 SHA-512。整合包仅保存为 `.mrpack`；PCL 2.99 官方版已禁用整合包导入，本工具不会绕过该限制。
- **直接下载**：对用户明确输入的 HTTP/HTTPS 链接下载文件。HTTP 会显示风险提示；已存在的文件不会被静默覆盖。
- 不收集账号凭据、不请求 Microsoft/Mojang 令牌、不启动游戏、不运行下载的文件。

## 安装

1. 关闭 PCL。
2. 将 `PCLDownloadFix.ps1` 复制到 **PCL 程序所在文件夹**（即 `{path}` 指向的目录）。`Custom.xaml` 可放在任意目录；若要使用 PCL 默认本地主页位置，可放到 `{path}PCL\Custom.xaml`。不要把脚本放入 `WindowsApps` 或 PCL 安装包内部。
3. 打开 PCL → **设置 → 个性化 → 自定义主页**，选择“读取本地文件”并加载 `Custom.xaml`，然后刷新主页。
4. 点主页的“打开下载中心”。工具启动时只为该次进程传入 `-ExecutionPolicy Bypass`，不会更改系统执行策略。
5. 原版下载先确认“Minecraft 游戏目录”。应选择 PCL 当前实例实际使用的游戏根目录；默认值 `%APPDATA%\.minecraft` 不一定是你的 PCL 目录。下载后在 PCL 刷新版本列表。
6. 注意：Modrinth 的 `.mrpack` 目前只会下载保存；PCL 2.99 官方版关闭了整合包导入，本工具不会绕过该限制。

## 重要限制

- 这是本地自定义主页 + 独立 PowerShell 下载工具，不是 PCL 官方插件 API。PCL 2.99.0.0 源码启用了 `RestrictMcDownload`，会禁用内置 Minecraft 下载及整合包导入；此工具不修改 PCL、不注入代码，也不绕过该导入限制。
- 原版安装仅补齐 Mojang 版本元数据描述的客户端、库、Assets 等文件；不安装 Forge、Fabric、NeoForge，不处理启动参数，不启动游戏。旧版自定义资源映射及个别非标准版本可能需额外处理。
- BMCLAPI 镜像可用性不保证；本次核验观察到部分端点返回 403/404。工具优先尝试官方源，镜像只作尽力回退，不代表必然可用。
- Modrinth 支持项目检索与文件下载；**不提供 CurseForge API 搜索或绕过其访问限制**。可以尝试使用“直接下载”，但需要你拥有合法可访问的直链。
- Mod/资源包下载后不会自动覆盖或改写实例；请自行放入正确实例目录。网络故障可取消后重试；取消无法中断正在进行的单个 HTTP 请求，单请求超时上限为 120 秒。
- 下载来自 Mojang、BMCLAPI、Modrinth、Modrinth 文件 CDN 镜像 `mod.mcimirror.top` 或你自行输入的站点。镜像文件按 Mojang/Modrinth 元数据校验；校验失败会被拒绝。直接链接没有可信哈希时无法证明文件来源和安全性。不要运行不信任的 JAR/EXE。
- 正版账户及游戏授权仍由用户自行负责；本工具不绕过正版验证。

## 验证状态

本交付包含源码。已通过 PowerShell AST 语法解析、XAML XML 结构检查、Assets/库镜像 URL 映射及路径穿越防护静态测试。当前工作区没有安装的 PCL 可供端到端验证，因此尚未声称已在实际 PCL 窗口加载或完成真实大文件下载；PCL 2.99 的整合包导入明确不可用。

## PCL 来源与许可说明

本工具独立实现下载逻辑，只参考 PCL 已公开的自定义事件说明、下载源 URL 映射和许可指南，没有复制 PCL 源码。PCL 项目作者为龙腾猫跃；上游仓库：<https://github.com/Meloong-Git/PCL>；赞助链接：<https://meloong.com/afd/a/LTCat>；上游许可/合理使用指南：<https://github.com/Meloong-Git/PCL/blob/main/LICENCE>。

本项目是涉及 Minecraft 下载功能的第三方扩展，名称明确标注 PCL 第三方属性。**本仓库为公开仓库，完整源码（`Custom.xaml`、`PCLDownloadFix.ps1`）已随 MIT 许可公开，可直接查看、修改与再分发；`v1.0.1` 压缩包只是便于安装的打包副本，不是唯一分发形式。** 公开分发时请继续逐项遵守 PCL 仓库当前的《合理使用指南》（署名、第三方命名、赞助劝导等），并注意 PCL 上游自身并非 MIT 许可。
