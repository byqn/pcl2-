@echo off
chcp 65001 >nul
echo 正在启动 PCL 下载恢复工具安装程序……
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0install.ps1"
if errorlevel 1 (
    echo.
    echo 安装没有完成，请查看上面的提示。
)
pause
