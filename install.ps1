# 一键安装：把 PCLDownloadRestore.exe 放进 PCL.exe 所在目录，把 Custom.xaml 放进 PCL\Custom.xaml。
# 需要与 PCLDownloadRestore.exe、Custom.xaml 放在同一文件夹；双击 install.cmd 运行即可。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'PCLDownloadRestore.exe'
$helper = Join-Path $here 'PCLDownloadFix.ps1'
$page = Join-Path $here 'Custom.xaml'

if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $page)) {
    [System.Windows.Forms.MessageBox]::Show("找不到 PCLDownloadRestore.exe 或 Custom.xaml。`n请把 install.cmd 与这两个文件放在同一个文件夹里再运行。", 'PCL 下载恢复工具 · 安装失败', 'OK', 'Error') | Out-Null
    exit 1
}

$dialog = New-Object System.Windows.Forms.OpenFileDialog
$dialog.Title = '请选择 PCL 启动器主程序（PCL.exe）'
$dialog.Filter = 'PCL 启动器 (PCL.exe)|PCL.exe|所有程序 (*.exe)|*.exe'
$dialog.CheckFileExists = $true
if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
    [System.Windows.Forms.MessageBox]::Show('已取消安装。', 'PCL 下载恢复工具', 'OK', 'Information') | Out-Null
    exit 0
}

$pclDir = Split-Path -Parent $dialog.FileName
if (-not (Test-Path -LiteralPath $pclDir)) {
    [System.Windows.Forms.MessageBox]::Show("PCL 目录不存在：$pclDir", '安装失败', 'OK', 'Error') | Out-Null
    exit 1
}

function Backup-ExistingFile([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backup = "$Path.bak-$stamp"
        Copy-Item -LiteralPath $Path -Destination $backup -Force
        return $backup
    }
    return $null
}

try {
    $exeTarget = Join-Path $pclDir 'PCLDownloadRestore.exe'
    $exeBackup = Backup-ExistingFile $exeTarget
    Copy-Item -LiteralPath $exe -Destination $exeTarget -Force

    $backups = @($exeBackup)

    if (Test-Path -LiteralPath $helper) {
        $helperTarget = Join-Path $pclDir 'PCLDownloadFix.ps1'
        $backups += Backup-ExistingFile $helperTarget
        Copy-Item -LiteralPath $helper -Destination $helperTarget -Force
    }

    $pageDir = Join-Path $pclDir 'PCL'
    if (-not (Test-Path -LiteralPath $pageDir)) {
        New-Item -ItemType Directory -Path $pageDir -Force | Out-Null
    }
    $pageTarget = Join-Path $pageDir 'Custom.xaml'
    $backups += Backup-ExistingFile $pageTarget
    Copy-Item -LiteralPath $page -Destination $pageTarget -Force

    $backupNote = ''
    $backupList = $backups | Where-Object { $_ }
    if ($backupList) {
        $backupNote = "`n`n原有文件已备份：`n" + ($backupList -join "`n")
    }

    [System.Windows.Forms.MessageBox]::Show(
        "安装完成！`n`nPCL 目录：$pclDir`n`n接下来：`n1. 重启 PCL（如果正在运行）`n2. 设置 → 个性化 → 主页 → 选择「本地文件」`n3. 点「刷新主页」`n4. 主页会出现「打开下载中心」按钮$backupNote",
        'PCL 下载恢复工具 · 安装完成', 'OK', 'Information') | Out-Null
    exit 0
}
catch {
    [System.Windows.Forms.MessageBox]::Show("安装失败：`n" + $_.Exception.Message, 'PCL 下载恢复工具', 'OK', 'Error') | Out-Null
    exit 1
}
