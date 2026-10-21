# Plain Craft Launcher (PCL) Download Restore — third-party helper
# Windows PowerShell 5.1, no external modules. Downloads official Minecraft files and Modrinth files.
# It does not launch Minecraft, modify PCL binaries, or execute downloaded files.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:AppName = 'Plain Craft Launcher (PCL) 下载恢复工具（第三方扩展）'
$script:UserAgent = 'PCL-Download-Restore/1.0.0 (local user initiated; https://github.com/Meloong-Git/PCL)'
$script:Cancelled = $false
$script:Busy = $false
$script:Projects = @()
$script:Versions = @()
$script:Ui = @{}

function Write-Log([string]$Text) {
    if ($script:Ui.Log) {
        $script:Ui.Log.AppendText("[$(Get-Date -Format 'HH:mm:ss')] $Text`r`n")
        $script:Ui.Log.SelectionStart = $script:Ui.Log.TextLength
        $script:Ui.Log.ScrollToCaret()
    }
    [System.Windows.Forms.Application]::DoEvents()
}

function Set-Progress([string]$Text, [int]$Current = 0, [int]$Total = 0) {
    if ($script:Ui.Status) { $script:Ui.Status.Text = $Text }
    if ($script:Ui.Progress) {
        if ($Total -gt 0) {
            $script:Ui.Progress.Style = 'Continuous'
            $script:Ui.Progress.Maximum = [Math]::Max(1, $Total)
            $script:Ui.Progress.Value = [Math]::Min([Math]::Max(0, $Current), $script:Ui.Progress.Maximum)
        } else {
            $script:Ui.Progress.Style = 'Marquee'
        }
    }
    [System.Windows.Forms.Application]::DoEvents()
    if ($script:Cancelled) { throw '已取消。' }
}

function Get-DownloadCandidates([string]$Url, [string]$Kind) {
    $result = New-Object 'System.Collections.Generic.List[string]'
    if ([string]::IsNullOrWhiteSpace($Url)) { return @() }
    $result.Add($Url)
    $mirror = $null
    switch ($Kind) {
        'meta' {
            $mirror = $Url.Replace('https://piston-data.mojang.com', 'https://bmclapi2.bangbang93.com').
                Replace('https://piston-meta.mojang.com', 'https://bmclapi2.bangbang93.com').
                Replace('https://launcher.mojang.com', 'https://bmclapi2.bangbang93.com').
                Replace('https://launchermeta.mojang.com', 'https://bmclapi2.bangbang93.com')
        }
        'asset' {
            $mirror = $Url.Replace('https://piston-data.mojang.com', 'https://bmclapi2.bangbang93.com/assets').
                Replace('https://piston-meta.mojang.com', 'https://bmclapi2.bangbang93.com/assets').
                Replace('https://resources.download.minecraft.net', 'https://bmclapi2.bangbang93.com/assets')
        }
        'library' {
            $mirror = $Url.Replace('https://piston-data.mojang.com', 'https://bmclapi2.bangbang93.com/maven').
                Replace('https://piston-meta.mojang.com', 'https://bmclapi2.bangbang93.com/maven').
                Replace('https://libraries.minecraft.net', 'https://bmclapi2.bangbang93.com/maven')
            $second = $Url.Replace('https://piston-data.mojang.com', 'https://bmclapi2.bangbang93.com/libraries').
                Replace('https://piston-meta.mojang.com', 'https://bmclapi2.bangbang93.com/libraries').
                Replace('https://libraries.minecraft.net', 'https://bmclapi2.bangbang93.com/libraries')
            if ($second -ne $Url -and -not $result.Contains($second)) { $result.Add($second) }
        }
        'modrinth' {
            $mirror = $Url.Replace('https://cdn.modrinth.com', 'https://mod.mcimirror.top')
        }
    }
    if ($mirror -and $mirror -ne $Url -and -not $result.Contains($mirror)) { $result.Add($mirror) }
    return $result.ToArray()
}

function Get-HashHex([string]$Path, [string]$Algorithm) {
    return (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}

function Invoke-VerifiedDownload(
    [string]$Url,
    [string]$Destination,
    [string]$ExpectedHash = '',
    [string]$HashAlgorithm = 'SHA1',
    [long]$ExpectedSize = 0,
    [string]$Kind = 'direct'
) {
    if ($script:Cancelled) { throw '已取消。' }
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    if ($ExpectedHash -and (Test-Path -LiteralPath $Destination)) {
        try {
            if ((Get-HashHex $Destination $HashAlgorithm) -eq $ExpectedHash.ToLowerInvariant()) {
                return '已存在且校验通过'
            }
        } catch { }
    }

    $lastError = $null
    foreach ($candidate in (Get-DownloadCandidates $Url $Kind)) {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            if ($script:Cancelled) { throw '已取消。' }
            $temp = "$Destination.pcldl-$([Guid]::NewGuid().ToString('N')).tmp"
            try {
                Write-Log "下载尝试 $attempt/3：$candidate"
                Invoke-WebRequest -Uri $candidate -OutFile $temp -UseBasicParsing -TimeoutSec 120 -Headers @{ 'User-Agent' = $script:UserAgent } | Out-Null
                if (-not (Test-Path -LiteralPath $temp)) { throw '服务端未生成下载文件。' }
                $length = (Get-Item -LiteralPath $temp).Length
                if ($ExpectedSize -gt 0 -and $length -ne $ExpectedSize) {
                    throw "文件大小不符，预期 $ExpectedSize 字节，实际 $length 字节。"
                }
                if ($ExpectedHash) {
                    $actualHash = Get-HashHex $temp $HashAlgorithm
                    if ($actualHash -ne $ExpectedHash.ToLowerInvariant()) {
                        throw "$HashAlgorithm 校验失败。"
                    }
                }
                if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
                Move-Item -LiteralPath $temp -Destination $Destination -Force
                return '下载完成并通过校验'
            } catch {
                $lastError = $_.Exception.Message
                if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
                Write-Log "失败：$lastError"
                if ($script:Cancelled) { throw '已取消。' }
            }
        }
    }
    throw "下载失败：$Url`n$lastError"
}

function Invoke-JsonRequest([string]$Url, [string]$Kind = 'meta') {
    $lastError = $null
    foreach ($candidate in (Get-DownloadCandidates $Url $Kind)) {
        try {
            return Invoke-RestMethod -Uri $candidate -Method Get -TimeoutSec 40 -Headers @{
                'User-Agent' = $script:UserAgent
                'Accept' = 'application/json'
            }
        } catch { $lastError = $_.Exception.Message; Write-Log "读取索引失败：$candidate ($lastError)" }
    }
    throw "无法获取索引：$Url`n$lastError"
}

function Get-SafeChildPath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "拒绝不安全的相对路径：$RelativePath"
    }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    if (-not $candidate.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径超出所选游戏目录：$RelativePath"
    }
    return $candidate
}

function Test-WindowsLibraryRules($Rules) {
    if (-not $Rules -or $Rules.Count -eq 0) { return $true }
    $allowed = $false
    foreach ($rule in $Rules) {
        $matches = $true
        if ($rule.os -and $rule.os.name -and $rule.os.name -ne 'windows') { $matches = $false }
        if ($rule.os -and $rule.os.arch) {
            $is64 = [Environment]::Is64BitOperatingSystem
            if ($rule.os.arch -eq 'x86' -and $is64) { $matches = $false }
            if ($rule.os.arch -in @('x86_64','x64') -and -not $is64) { $matches = $false }
        }
        if ($rule.features -and $rule.features.PSObject.Properties.Count -gt 0) { $matches = $false }
        if ($matches) { $allowed = ($rule.action -eq 'allow') }
    }
    return $allowed
}

function Get-NativeClassifier([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    $arch = if ([Environment]::Is64BitOperatingSystem) { '64' } else { '32' }
    return $Name.Replace('${arch}', $arch)
}

function Get-SelectedRoot {
    $root = [string]$script:Ui.GameRoot.Text
    if ([string]::IsNullOrWhiteSpace($root)) { throw '请先选择 Minecraft 游戏目录。' }
    $root = [IO.Path]::GetFullPath($root)
    if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root -Force | Out-Null }
    return $root
}

function Download-MinecraftVersion($Entry) {
    $root = Get-SelectedRoot
    $id = [string]$Entry.id
    if ($id -notmatch '^[A-Za-z0-9._-]+$') { throw "版本标识异常：$id" }
    $versionDir = Join-Path (Join-Path $root 'versions') $id
    New-Item -ItemType Directory -Path $versionDir -Force | Out-Null
    $jsonPath = Join-Path $versionDir "$id.json"
    $jarPath = Join-Path $versionDir "$id.jar"

    Set-Progress "获取 $id 的版本元数据…"
    Invoke-VerifiedDownload ([string]$Entry.url) $jsonPath ([string]$Entry.sha1) 'SHA1' 0 'meta' | Out-Null
    $meta = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$meta.id -ne $id) { throw "版本元数据 ID 不匹配：$($meta.id)" }

    if (-not $meta.downloads -or -not $meta.downloads.client) { throw '该版本元数据没有官方客户端 JAR 下载信息。' }
    $client = $meta.downloads.client
    Set-Progress "下载 $id 客户端 JAR…"
    Invoke-VerifiedDownload ([string]$client.url) $jarPath ([string]$client.sha1) 'SHA1' ([long]$client.size) 'meta' | Out-Null

    $libraries = @($meta.libraries)
    $tasks = New-Object 'System.Collections.Generic.List[object]'
    foreach ($lib in $libraries) {
        if (-not (Test-WindowsLibraryRules $lib.rules)) { continue }
        if ($lib.downloads.artifact -and $lib.downloads.artifact.path -and $lib.downloads.artifact.url) {
            $artifact = $lib.downloads.artifact
            $path = Get-SafeChildPath $root ([string]$artifact.path)
            $tasks.Add([pscustomobject]@{ Url=[string]$artifact.url; Path=$path; Hash=[string]$artifact.sha1; Size=[long]$artifact.size; Kind='library'; Label=[string]$lib.name })
        }
        if ($lib.natives.windows -and $lib.downloads.classifiers) {
            $classifierName = Get-NativeClassifier ([string]$lib.natives.windows)
            $classifier = $lib.downloads.classifiers.PSObject.Properties[$classifierName].Value
            if ($classifier -and $classifier.path -and $classifier.url) {
                $path = Get-SafeChildPath $root ([string]$classifier.path)
                $tasks.Add([pscustomobject]@{ Url=[string]$classifier.url; Path=$path; Hash=[string]$classifier.sha1; Size=[long]$classifier.size; Kind='library'; Label="$($lib.name) [$classifierName]" })
            }
        }
    }

    if ($script:Ui.IncludeAssets.Checked -and $meta.assetIndex -and $meta.assetIndex.url) {
        $assetIndex = $meta.assetIndex
        $indexFile = Get-SafeChildPath $root "assets/indexes/$($assetIndex.id).json"
        Set-Progress "下载资源索引 $($assetIndex.id)…"
        Invoke-VerifiedDownload ([string]$assetIndex.url) $indexFile ([string]$assetIndex.sha1) 'SHA1' ([long]$assetIndex.size) 'meta' | Out-Null
        $index = Get-Content -LiteralPath $indexFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($property in $index.objects.PSObject.Properties) {
            $object = $property.Value
            $hash = [string]$object.hash
            if ($hash -notmatch '^[a-fA-F0-9]{40}$') { throw "资源索引中出现无效 SHA-1：$($property.Name)" }
            $relative = "assets/objects/$($hash.Substring(0,2))/$hash"
            $dest = Get-SafeChildPath $root $relative
            $url = "https://resources.download.minecraft.net/$($hash.Substring(0,2))/$hash"
            $tasks.Add([pscustomobject]@{ Url=$url; Path=$dest; Hash=$hash; Size=[long]$object.size; Kind='asset'; Label=[string]$property.Name })
        }
    }

    if ($meta.logging -and $meta.logging.client -and $meta.logging.client.file -and $meta.logging.client.file.url) {
        $logFile = $meta.logging.client.file
        $logPath = Get-SafeChildPath $root "assets/log_configs/$($logFile.id)"
        $tasks.Add([pscustomobject]@{ Url=[string]$logFile.url; Path=$logPath; Hash=[string]$logFile.sha1; Size=[long]$logFile.size; Kind='meta'; Label='logging config' })
    }

    $total = $tasks.Count
    $i = 0
    foreach ($task in $tasks) {
        $i++
        Set-Progress "下载依赖与资源：$i / $total — $($task.Label)" $i $total
        Invoke-VerifiedDownload $task.Url $task.Path $task.Hash 'SHA1' $task.Size $task.Kind | Out-Null
        if (($i % 20) -eq 0) { Write-Log "已完成 $i / $total 个依赖/资源文件。" }
    }
    Set-Progress "版本 $id 的官方文件已下载完成。" $total $total
    Write-Log "完成：$id；根目录：$root。PCL 中刷新版本列表后即可检查是否识别。"
    [System.Windows.Forms.MessageBox]::Show("版本 $id 的官方文件已下载到：`n$root`n`n本工具不会启动游戏。请在 PCL 中刷新版本列表；首次启动仍需正版账号验证。", $script:AppName, 'OK', 'Information') | Out-Null
}

function Load-MinecraftVersions {
    Set-Progress '正在读取 Mojang 版本清单…'
    $manifest = Invoke-JsonRequest 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json' 'meta'
    $script:Versions = @($manifest.versions)
    $script:Ui.Version.Items.Clear()
    foreach ($version in $script:Versions) {
        [void]$script:Ui.Version.Items.Add("$($version.id)  [$($version.type)]")
    }
    $release = [string]$manifest.latest.release
    for ($i = 0; $i -lt $script:Versions.Count; $i++) {
        if ($script:Versions[$i].id -eq $release) { $script:Ui.Version.SelectedIndex = $i; break }
    }
    Set-Progress "已载入 $($script:Versions.Count) 个版本。" $script:Versions.Count $script:Versions.Count
    Write-Log "版本清单载入完成，最新正式版：$release。"
}

function Get-ModrinthProjectType {
    switch ([string]$script:Ui.ProjectType.SelectedItem) {
        '整合包' { return 'modpack' }
        'Mod' { return 'mod' }
        '资源包' { return 'resourcepack' }
        '光影包' { return 'shader' }
        default { return 'modpack' }
    }
}

function Search-Modrinth {
    $query = [string]$script:Ui.Search.Text
    if ([string]::IsNullOrWhiteSpace($query)) { throw '请输入搜索关键词。' }
    $type = Get-ModrinthProjectType
    $encodedQuery = [Uri]::EscapeDataString($query)
    $facets = [Uri]::EscapeDataString(('[["project_type:' + $type + '"]]'))
    $url = "https://api.modrinth.com/v2/search?query=$encodedQuery&facets=$facets&limit=30&index=relevance"
    Set-Progress "正在 Modrinth 搜索 $type…"
    $response = Invoke-JsonRequest $url 'direct'
    $script:Projects = @($response.hits)
    $script:Ui.Results.Items.Clear()
    foreach ($project in $script:Projects) {
        [void]$script:Ui.Results.Items.Add("$($project.title)  —  $($project.description)")
    }
    Set-Progress "找到 $($script:Projects.Count) 个项目。" $script:Projects.Count $script:Projects.Count
    Write-Log "Modrinth 搜索完成：$($script:Projects.Count) 项。"
}

function Download-ModrinthProject {
    $index = $script:Ui.Results.SelectedIndex
    if ($index -lt 0 -or $index -ge $script:Projects.Count) { throw '请先选择一个搜索结果。' }
    $project = $script:Projects[$index]
    $versionUrl = "https://api.modrinth.com/v2/project/$([Uri]::EscapeDataString([string]$project.project_id))/version"
    Set-Progress "读取 $($project.title) 的可用版本…"
    $versions = @(Invoke-JsonRequest $versionUrl 'direct')
    $gameVersion = ([string]$script:Ui.GameVersion.Text).Trim()
    $loader = ([string]$script:Ui.Loader.Text).Trim().ToLowerInvariant()
    if ($gameVersion) { $versions = @($versions | Where-Object { $_.game_versions -contains $gameVersion }) }
    if ($loader -and $loader -ne '任意') { $versions = @($versions | Where-Object { $_.loaders -contains $loader }) }
    if (-not $versions -or $versions.Count -eq 0) { throw '没有找到符合游戏版本/加载器条件的发布版本。清空筛选项后重试。' }
    $version = $versions[0]
    $file = @($version.files | Sort-Object @{ Expression = { if ($_.primary) { 0 } else { 1 } } })[0]
    if (-not $file -or -not $file.url) { throw '所选发布版本没有可下载文件。' }

    $destinationRoot = [IO.Path]::GetFullPath([string]$script:Ui.Output.Text)
    if ([string]::IsNullOrWhiteSpace($destinationRoot)) { throw '请设置下载保存目录。' }
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    $fileName = [IO.Path]::GetFileName([string]$file.filename)
    if (-not $fileName -or $fileName -match '[\\/:*?"<>|]') { throw 'Modrinth 返回的文件名无效。' }
    $destination = Join-Path $destinationRoot $fileName
    if (Test-Path -LiteralPath $destination) {
        $base = [IO.Path]::GetFileNameWithoutExtension($fileName)
        $ext = [IO.Path]::GetExtension($fileName)
        $destination = Join-Path $destinationRoot ("{0}-{1}{2}" -f $base, (Get-Date -Format 'yyyyMMdd-HHmmss'), $ext)
    }
    $sha512 = if ($file.hashes) { [string]$file.hashes.sha512 } else { '' }
    if ($sha512 -notmatch '^[a-fA-F0-9]{128}$') { throw 'Modrinth API 未返回可用的 SHA-512；拒绝下载。' }
    Set-Progress "下载 Modrinth 文件：$fileName"
    Invoke-VerifiedDownload ([string]$file.url) $destination $sha512 'SHA512' ([long]$file.size) 'modrinth' | Out-Null
    Set-Progress '下载完成。' 1 1
    Write-Log "已保存：$destination"
    if ((Get-ModrinthProjectType) -eq 'modpack') {
        [System.Windows.Forms.MessageBox]::Show("整合包已保存：`n$destination`n`n注意：PCL 2.99 官方版禁用了整合包导入。本工具只下载文件，不绕过该限制。", $script:AppName, 'OK', 'Information') | Out-Null
    } else {
        [System.Windows.Forms.MessageBox]::Show("文件已保存并通过 SHA-512 校验：`n$destination`n`n请按对应加载器/资源类型手动放入正确目录；本工具不会自动覆盖现有 Mod。", $script:AppName, 'OK', 'Information') | Out-Null
    }
}

function Download-DirectFile {
    $urlText = ([string]$script:Ui.DirectUrl.Text).Trim()
    $uri = $null
    if (-not [Uri]::TryCreate($urlText, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('https','http')) {
        throw '请输入完整的 HTTP 或 HTTPS 下载链接。'
    }
    if ($uri.Scheme -eq 'http') {
        $answer = [System.Windows.Forms.MessageBox]::Show('该链接未加密（HTTP），下载内容可能被篡改。仍要继续吗？', $script:AppName, 'YesNo', 'Warning')
        if ($answer -ne 'Yes') { return }
    }
    $folder = [IO.Path]::GetFullPath([string]$script:Ui.Output.Text)
    if ([string]::IsNullOrWhiteSpace($folder)) { throw '请设置下载保存目录。' }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $name = ([string]$script:Ui.DirectName.Text).Trim()
    if (-not $name) { $name = [IO.Path]::GetFileName($uri.AbsolutePath) }
    if (-not $name -or $name -match '[\\/:*?"<>|]' -or $name -in @('.','..')) { throw '文件名无效，请填写简单文件名。' }
    $dest = Join-Path $folder $name
    if (Test-Path -LiteralPath $dest) {
        $base = [IO.Path]::GetFileNameWithoutExtension($name)
        $ext = [IO.Path]::GetExtension($name)
        $dest = Join-Path $folder ("{0}-{1}{2}" -f $base, (Get-Date -Format 'yyyyMMdd-HHmmss'), $ext)
    }
    Set-Progress "下载文件：$($uri.Host)"
    Invoke-VerifiedDownload $uri.AbsoluteUri $dest '' 'SHA256' 0 'direct' | Out-Null
    Set-Progress '下载完成。' 1 1
    Write-Log "已保存：$dest"
    [System.Windows.Forms.MessageBox]::Show("下载完成：`n$dest", $script:AppName, 'OK', 'Information') | Out-Null
}

function Run-UiOperation([scriptblock]$Operation) {
    if ($script:Busy) { return }
    $script:Busy = $true
    $script:Cancelled = $false
    $script:Ui.Cancel.Enabled = $true
    $script:Ui.RunVanilla.Enabled = $false
    $script:Ui.SearchButton.Enabled = $false
    $script:Ui.DownloadProject.Enabled = $false
    $script:Ui.DirectButton.Enabled = $false
    try { & $Operation }
    catch {
        Write-Log "操作失败：$($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, $script:AppName, 'OK', 'Error') | Out-Null
    }
    finally {
        $script:Busy = $false
        $script:Ui.Cancel.Enabled = $false
        $script:Ui.RunVanilla.Enabled = $true
        $script:Ui.SearchButton.Enabled = $true
        $script:Ui.DownloadProject.Enabled = $true
        $script:Ui.DirectButton.Enabled = $true
        $script:Ui.Progress.Style = 'Continuous'
        $script:Ui.Progress.Value = 0
    }
}

# ---------- UI ----------
$form = New-Object Windows.Forms.Form
$form.Text = $script:AppName
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object Drawing.Size(900, 900)
$form.MinimumSize = New-Object Drawing.Size(780, 820)
$form.Font = New-Object Drawing.Font('Segoe UI', 9)
$form.BackColor = [Drawing.Color]::FromArgb(245, 248, 252)
$form.ForeColor = [Drawing.Color]::FromArgb(32, 43, 58)
$script:Ui.Form = $form

$header = New-Object Windows.Forms.Label
$header.Text = '下载中心 · 官方文件校验 · 镜像失败自动重试'
$header.Font = New-Object Drawing.Font('Segoe UI', 15, [Drawing.FontStyle]::Bold)
$header.ForeColor = [Drawing.Color]::FromArgb(35, 93, 170)
$header.SetBounds(18, 14, 800, 32)
$form.Controls.Add($header)

$rootLabel = New-Object Windows.Forms.Label
$rootLabel.Text = 'Minecraft 游戏目录（需选包含/将包含 versions、libraries、assets 的根目录）'
$rootLabel.SetBounds(20, 54, 700, 20)
$form.Controls.Add($rootLabel)
$gameRoot = New-Object Windows.Forms.TextBox
$gameRoot.Text = Join-Path $env:APPDATA '.minecraft'
$gameRoot.SetBounds(20, 76, 735, 26)
$form.Controls.Add($gameRoot)
$browseRoot = New-Object Windows.Forms.Button
$browseRoot.Text = '选择…'
$browseRoot.SetBounds(765, 75, 95, 28)
$form.Controls.Add($browseRoot)
$browseRoot.Add_Click({
    $dialog = New-Object Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择 PCL 当前游戏目录（实例根目录）'
    if (Test-Path -LiteralPath $script:Ui.GameRoot.Text) { $dialog.SelectedPath = $script:Ui.GameRoot.Text }
    if ($dialog.ShowDialog() -eq 'OK') { $script:Ui.GameRoot.Text = $dialog.SelectedPath }
})

$tab = New-Object Windows.Forms.TabControl
$tab.SetBounds(18, 112, 844, 420)
$form.Controls.Add($tab)

$tabVanilla = New-Object Windows.Forms.TabPage
$tabVanilla.Text = '原版 Minecraft'
$tab.Controls.Add($tabVanilla)
$vanillaInfo = New-Object Windows.Forms.Label
$vanillaInfo.Text = '从 Mojang 版本清单获取版本元数据、客户端 JAR、Windows 库和可选 Assets；官方源失败时尝试 BMCLAPI。所有有 SHA-1 的文件均校验。'
$vanillaInfo.AutoSize = $false
$vanillaInfo.SetBounds(16, 16, 790, 54)
$tabVanilla.Controls.Add($vanillaInfo)
$loadVersions = New-Object Windows.Forms.Button
$loadVersions.Text = '读取版本列表'
$loadVersions.SetBounds(16, 82, 130, 30)
$tabVanilla.Controls.Add($loadVersions)
$version = New-Object Windows.Forms.ComboBox
$version.DropDownStyle = 'DropDownList'
$version.SetBounds(160, 84, 360, 26)
$tabVanilla.Controls.Add($version)
$includeAssets = New-Object Windows.Forms.CheckBox
$includeAssets.Text = '下载游戏资源（完整启动通常需要，文件较多）'
$includeAssets.Checked = $true
$includeAssets.SetBounds(18, 132, 500, 26)
$tabVanilla.Controls.Add($includeAssets)
$runVanilla = New-Object Windows.Forms.Button
$runVanilla.Text = '下载所选版本文件'
$runVanilla.BackColor = [Drawing.Color]::FromArgb(42, 111, 190)
$runVanilla.ForeColor = [Drawing.Color]::White
$runVanilla.FlatStyle = 'Flat'
$runVanilla.SetBounds(16, 180, 190, 38)
$tabVanilla.Controls.Add($runVanilla)
$vanillaNote = New-Object Windows.Forms.Label
$vanillaNote.Text = '说明：仅补齐官方原版文件，不安装 Forge/Fabric/NeoForge，不启动游戏，不绕过正版验证。下载完成后在 PCL 刷新版本列表。'
$vanillaNote.SetBounds(16, 235, 790, 45)
$tabVanilla.Controls.Add($vanillaNote)

$tabMods = New-Object Windows.Forms.TabPage
$tabMods.Text = 'Modrinth / 整合包'
$tab.Controls.Add($tabMods)
$typeLabel = New-Object Windows.Forms.Label
$typeLabel.Text = '类型'
$typeLabel.SetBounds(16, 18, 45, 24)
$tabMods.Controls.Add($typeLabel)
$projectType = New-Object Windows.Forms.ComboBox
$projectType.DropDownStyle = 'DropDownList'
[void]$projectType.Items.AddRange([string[]]@('整合包','Mod','资源包','光影包'))
$projectType.SelectedIndex = 0
$projectType.SetBounds(60, 14, 120, 27)
$tabMods.Controls.Add($projectType)
$searchLabel = New-Object Windows.Forms.Label
$searchLabel.Text = '关键词'
$searchLabel.SetBounds(192, 18, 55, 24)
$tabMods.Controls.Add($searchLabel)
$search = New-Object Windows.Forms.TextBox
$search.SetBounds(248, 14, 380, 27)
$tabMods.Controls.Add($search)
$searchButton = New-Object Windows.Forms.Button
$searchButton.Text = '搜索'
$searchButton.SetBounds(640, 13, 110, 30)
$tabMods.Controls.Add($searchButton)
$resultsLabel = New-Object Windows.Forms.Label
$resultsLabel.Text = '搜索结果（选择项目后下载最新兼容文件）'
$resultsLabel.SetBounds(16, 52, 400, 22)
$tabMods.Controls.Add($resultsLabel)
$results = New-Object Windows.Forms.ListBox
$results.SetBounds(16, 77, 790, 190)
$results.HorizontalScrollbar = $true
$tabMods.Controls.Add($results)
$gameVersionLabel = New-Object Windows.Forms.Label
$gameVersionLabel.Text = '游戏版本筛选（可空）'
$gameVersionLabel.SetBounds(16, 278, 155, 22)
$tabMods.Controls.Add($gameVersionLabel)
$gameVersion = New-Object Windows.Forms.TextBox
$gameVersion.SetBounds(170, 275, 145, 27)
$tabMods.Controls.Add($gameVersion)
$loaderLabel = New-Object Windows.Forms.Label
$loaderLabel.Text = '加载器筛选（fabric/forge/neoforge/quilt，可空）'
$loaderLabel.SetBounds(330, 278, 320, 22)
$tabMods.Controls.Add($loaderLabel)
$loader = New-Object Windows.Forms.TextBox
$loader.SetBounds(650, 275, 145, 27)
$tabMods.Controls.Add($loader)
$downloadProject = New-Object Windows.Forms.Button
$downloadProject.Text = '下载所选项目'
$downloadProject.BackColor = [Drawing.Color]::FromArgb(42, 111, 190)
$downloadProject.ForeColor = [Drawing.Color]::White
$downloadProject.FlatStyle = 'Flat'
$downloadProject.SetBounds(16, 318, 165, 36)
$tabMods.Controls.Add($downloadProject)
$modrinthNote = New-Object Windows.Forms.Label
$modrinthNote.Text = '整合包下载为 .mrpack 并保存到所选目录；PCL 2.99 官方版禁用了导入，本工具不会绕过限制。Mod/资源文件也保存到所选目录，需手动放置。Modrinth 文件按 SHA-512 校验。'
$modrinthNote.SetBounds(195, 313, 610, 52)
$tabMods.Controls.Add($modrinthNote)

$tabDirect = New-Object Windows.Forms.TabPage
$tabDirect.Text = '直接下载'
$tab.Controls.Add($tabDirect)
$directInfo = New-Object Windows.Forms.Label
$directInfo.Text = '下载一个你信任来源的 HTTP/HTTPS 文件。此工具只保存文件，不运行它；HTTP 会提示风险。'
$directInfo.SetBounds(16, 18, 790, 42)
$tabDirect.Controls.Add($directInfo)
$directUrlLabel = New-Object Windows.Forms.Label
$directUrlLabel.Text = '下载链接'
$directUrlLabel.SetBounds(16, 76, 90, 24)
$tabDirect.Controls.Add($directUrlLabel)
$directUrl = New-Object Windows.Forms.TextBox
$directUrl.SetBounds(108, 72, 690, 27)
$tabDirect.Controls.Add($directUrl)
$directNameLabel = New-Object Windows.Forms.Label
$directNameLabel.Text = '保存文件名（可空）'
$directNameLabel.SetBounds(16, 118, 140, 24)
$tabDirect.Controls.Add($directNameLabel)
$directName = New-Object Windows.Forms.TextBox
$directName.SetBounds(160, 114, 360, 27)
$tabDirect.Controls.Add($directName)
$directButton = New-Object Windows.Forms.Button
$directButton.Text = '下载文件'
$directButton.BackColor = [Drawing.Color]::FromArgb(42, 111, 190)
$directButton.ForeColor = [Drawing.Color]::White
$directButton.FlatStyle = 'Flat'
$directButton.SetBounds(16, 164, 145, 36)
$tabDirect.Controls.Add($directButton)

$outputLabel = New-Object Windows.Forms.Label
$outputLabel.Text = 'Modrinth / 直接下载保存到'
$outputLabel.SetBounds(20, 542, 300, 22)
$form.Controls.Add($outputLabel)
$output = New-Object Windows.Forms.TextBox
$output.Text = Join-Path $env:USERPROFILE 'Downloads\PCL-Download-Restore'
$output.SetBounds(20, 566, 735, 26)
$form.Controls.Add($output)
$browseOutput = New-Object Windows.Forms.Button
$browseOutput.Text = '选择…'
$browseOutput.SetBounds(765, 565, 95, 28)
$form.Controls.Add($browseOutput)
$browseOutput.Add_Click({
    $dialog = New-Object Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择下载文件保存目录'
    if (Test-Path -LiteralPath $script:Ui.Output.Text) { $dialog.SelectedPath = $script:Ui.Output.Text }
    if ($dialog.ShowDialog() -eq 'OK') { $script:Ui.Output.Text = $dialog.SelectedPath }
})

$status = New-Object Windows.Forms.Label
$status.Text = '就绪。先确认游戏目录；不确定时可先只下载 Modrinth/直接文件。'
$status.SetBounds(20, 600, 730, 22)
$form.Controls.Add($status)
$progress = New-Object Windows.Forms.ProgressBar
$progress.SetBounds(20, 623, 735, 18)
$form.Controls.Add($progress)
$cancel = New-Object Windows.Forms.Button
$cancel.Text = '取消'
$cancel.Enabled = $false
$cancel.SetBounds(765, 613, 95, 30)
$form.Controls.Add($cancel)
$log = New-Object Windows.Forms.TextBox
$log.Multiline = $true
$log.ReadOnly = $true
$log.ScrollBars = 'Vertical'
$log.Font = New-Object Drawing.Font('Consolas', 8)
$log.SetBounds(20, 648, 840, 180)
$log.Visible = $true
$form.Controls.Add($log)
# Reserve a visible diagnostics area below the tabs.
$form.ClientSize = New-Object Drawing.Size(880, 840)

$script:Ui.GameRoot = $gameRoot
$script:Ui.Output = $output
$script:Ui.Version = $version
$script:Ui.IncludeAssets = $includeAssets
$script:Ui.RunVanilla = $runVanilla
$script:Ui.ProjectType = $projectType
$script:Ui.Search = $search
$script:Ui.Results = $results
$script:Ui.GameVersion = $gameVersion
$script:Ui.Loader = $loader
$script:Ui.SearchButton = $searchButton
$script:Ui.DownloadProject = $downloadProject
$script:Ui.DirectUrl = $directUrl
$script:Ui.DirectName = $directName
$script:Ui.DirectButton = $directButton
$script:Ui.Status = $status
$script:Ui.Progress = $progress
$script:Ui.Cancel = $cancel
$script:Ui.Log = $log

$loadVersions.Add_Click({ Run-UiOperation { Load-MinecraftVersions } })
$runVanilla.Add_Click({
    Run-UiOperation {
        $idx = $script:Ui.Version.SelectedIndex
        if ($idx -lt 0 -or $idx -ge $script:Versions.Count) { throw '请先读取并选择 Minecraft 版本。' }
        Download-MinecraftVersion $script:Versions[$idx]
    }
})
$searchButton.Add_Click({ Run-UiOperation { Search-Modrinth } })
$downloadProject.Add_Click({ Run-UiOperation { Download-ModrinthProject } })
$directButton.Add_Click({ Run-UiOperation { Download-DirectFile } })
$cancel.Add_Click({ $script:Cancelled = $true; $status.Text = '正在取消…' })

Write-Log 'PCL 下载恢复工具已启动。此工具不启动游戏、不执行下载内容，也不要求 Microsoft/Mojang 登录令牌。'
Write-Log '原版下载会写入所选游戏根目录；建议先备份现有实例目录。'
[void]$form.ShowDialog()
