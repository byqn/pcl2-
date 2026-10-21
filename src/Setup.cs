// PCL 下载恢复工具 · 安装程序
// 编译（在仓库根目录执行）：
//   csc /target:winexe /codepage:65001 /out:PCLDownloadRestore-Setup.exe ^
//       /resource:Custom.xaml,CustomPage /resource:PCLDownloadRestore.exe,Payload ^
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Setup.cs
// 安装内容已嵌入本 exe，安装时只需选择 PCL.exe。

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PclDownloadRestoreSetup
{
    internal static class Program
    {
        internal const string AppName = "PCL 下载恢复工具 · 安装程序";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Label _status;
        private readonly Button _install;
        private readonly Button _close;

        public SetupForm()
        {
            Text = Program.AppName;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 330);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 248, 252);

            var title = new Label();
            title.Text = "PCL 下载恢复工具";
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(35, 93, 170);
            title.SetBounds(24, 20, 500, 34);
            Controls.Add(title);

            var subtitle = new Label();
            subtitle.Text = "第三方扩展 · 独立下载中心（exe）";
            subtitle.ForeColor = Color.FromArgb(90, 100, 115);
            subtitle.SetBounds(26, 56, 500, 22);
            Controls.Add(subtitle);

            var body = new Label();
            body.Text = "本安装程序会把以下内容安装到 PCL.exe 所在文件夹：\r\n" +
                        "  • PCLDownloadRestore.exe —— 下载中心主程序\r\n" +
                        "  • PCL\\Custom.xaml —— PCL 本地自定义主页\r\n\r\n" +
                        "已有同名文件会先备份为 .bak-时间戳。\r\n" +
                        "安装后可随时删除这些文件来卸载本扩展。";
            body.SetBounds(26, 92, 510, 120);
            Controls.Add(body);

            var note = new Label();
            note.Text = "注意：PCL 2.99 官方版已禁用 Minecraft 下载与整合包导入；\r\n本扩展不修改 PCL、不绕过该限制，只提供独立的下载工具。";
            note.ForeColor = Color.FromArgb(150, 95, 30);
            note.SetBounds(26, 214, 510, 40);
            Controls.Add(note);

            _status = new Label();
            _status.Text = "请选择你的 PCL.exe 开始安装。";
            _status.SetBounds(26, 258, 510, 22);
            Controls.Add(_status);

            _install = new Button();
            _install.Text = "选择 PCL.exe 并安装";
            _install.SetBounds(26, 286, 190, 34);
            Controls.Add(_install);

            _close = new Button();
            _close.Text = "取消";
            _close.SetBounds(438, 286, 98, 34);
            Controls.Add(_close);

            _install.Click += delegate { Install(); };
            _close.Click += delegate { Close(); };
        }

        private void Install()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "请选择 PCL 启动器主程序（PCL.exe）";
                dialog.Filter = "PCL 启动器 (PCL.exe)|PCL.exe|所有程序 (*.exe)|*.exe";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string pclDir;
                try
                {
                    pclDir = Path.GetDirectoryName(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法解析 PCL 目录：" + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (string.IsNullOrEmpty(pclDir) || !Directory.Exists(pclDir))
                {
                    MessageBox.Show(this, "PCL 目录不存在。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                try
                {
                    _install.Enabled = false;
                    _status.Text = "正在安装到：" + pclDir;
                    Application.DoEvents();

                    string exeTarget = Path.Combine(pclDir, "PCLDownloadRestore.exe");
                    string pageTarget = Path.Combine(Path.Combine(pclDir, "PCL"), "Custom.xaml");
                    var backups = new System.Collections.Generic.List<string>();

                    Backup(exeTarget, backups);
                    Extract("Payload", exeTarget);

                    Directory.CreateDirectory(Path.GetDirectoryName(pageTarget));
                    Backup(pageTarget, backups);
                    Extract("CustomPage", pageTarget);

                    string extra = "";
                    if (backups.Count > 0)
                    {
                        extra = "\r\n\r\n原有文件已备份：\r\n" + string.Join("\r\n", backups.ToArray());
                    }

                    _status.Text = "安装完成。";
                    MessageBox.Show(this,
                        "安装完成！\r\n\r\nPCL 目录：" + pclDir + "\r\n\r\n接下来：" +
                        "\r\n1. 重启 PCL（如果正在运行）" +
                        "\r\n2. 设置 → 个性化 → 主页 → 选择「本地文件」" +
                        "\r\n3. 点「刷新主页」" +
                        "\r\n4. 主页会出现「打开下载中心」按钮" + extra,
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    _status.Text = "安装失败。";
                    MessageBox.Show(this, "安装失败：\r\n" + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _install.Enabled = true;
                }
            }
        }

        private static void Backup(string path, System.Collections.Generic.List<string> backups)
        {
            if (!File.Exists(path)) return;
            string backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Copy(path, backup, true);
                backups.Add(backup);
            }
            catch
            {
                // 备份失败不阻止安装，继续覆盖。
            }
        }

        private static void Extract(string resourceName, string targetPath)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new Exception("安装包缺少内嵌资源：" + resourceName);
                string temp = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(output);
                }
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(temp, targetPath);
            }
        }
    }
}
