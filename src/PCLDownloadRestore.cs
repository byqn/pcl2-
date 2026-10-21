// PCL 下载恢复工具（第三方扩展）v1.1.0
// 编译：csc /target:winexe /codepage:65001 /out:dist\PCLDownloadRestore.exe src\PCLDownloadRestore.cs
// 目标框架：.NET Framework 3.5+（Windows 10/11 自带 4.8 可直接运行）
// 本程序只下载文件，不启动游戏、不执行下载内容、不修改 PCL。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PclDownloadRestore
{
    internal static class Program
    {
        internal const string AppName = "PCL 下载恢复工具 v1.1.0（第三方扩展）";
        internal const string UserAgent = "PCL-Download-Restore/1.1.0";

        [STAThread]
        private static void Main()
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            catch { }
            ServicePointManager.Expect100Continue = false;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal interface IProgressSink
    {
        void Log(string text);
        void Status(string text);
        void Progress(int current, int total);
    }

    internal sealed class DownloadTask
    {
        public string Url { get; set; }
        public string Path { get; set; }
        public string Hash { get; set; }
        public string Algo { get; set; }
        public long Size { get; set; }
        public string Kind { get; set; }
        public string Label { get; set; }
    }

    internal sealed class VersionEntry
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
        public string Sha1 { get; set; }
        public override string ToString()
        {
            return Id + "  [" + Type + "]";
        }
    }

    internal sealed class ProjectEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public override string ToString()
        {
            return Title + "  —  " + Description;
        }
    }

    internal static class Engine
    {
        public static string[] Candidates(string url, string kind)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(url)) return list.ToArray();
            list.Add(url);
            string mirror = null;
            switch (kind)
            {
                case "meta":
                    mirror = url
                        .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com")
                        .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com")
                        .Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com")
                        .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com");
                    break;
                case "asset":
                    mirror = url
                        .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/assets")
                        .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com/assets")
                        .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets");
                    break;
                case "library":
                    mirror = url
                        .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/maven")
                        .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com/maven")
                        .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven");
                    var second = url
                        .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/libraries")
                        .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com/libraries")
                        .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/libraries");
                    if (second != url && !list.Contains(second)) list.Add(second);
                    break;
                case "modrinth":
                    mirror = url.Replace("https://cdn.modrinth.com", "https://mod.mcimirror.top");
                    break;
            }
            if (mirror != null && mirror != url && !list.Contains(mirror)) list.Add(mirror);
            return list.ToArray();
        }

        public static string HashFile(string path, string algo)
        {
            HashAlgorithm sha;
            if (string.Equals(algo, "SHA512", StringComparison.OrdinalIgnoreCase)) sha = SHA512.Create();
            else sha = SHA1.Create();
            using (sha)
            using (var fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static void Download(string url, string destination, string expectedHash, string algo, long expectedSize, string kind, IProgressSink sink, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string parent = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (!string.IsNullOrEmpty(expectedHash) && File.Exists(destination))
            {
                try
                {
                    if (string.Equals(HashFile(destination, algo), expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        sink.Log("已存在且校验通过：" + System.IO.Path.GetFileName(destination));
                        return;
                    }
                }
                catch { }
            }

            Exception lastError = null;
            foreach (string candidate in Candidates(url, kind))
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    string temp = destination + ".part-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        sink.Log(string.Format("下载尝试 {0}/3：{1}", attempt, candidate));
                        DownloadOnce(candidate, temp, sink, token);
                        if (!File.Exists(temp)) throw new Exception("服务端未生成下载文件。");
                        long length = new FileInfo(temp).Length;
                        if (expectedSize > 0 && length != expectedSize)
                            throw new Exception(string.Format("文件大小不符：预期 {0} 字节，实际 {1} 字节。", expectedSize, length));
                        if (!string.IsNullOrEmpty(expectedHash))
                        {
                            string actual = HashFile(temp, algo);
                            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                                throw new Exception(algo + " 校验失败。");
                        }
                        if (File.Exists(destination)) File.Delete(destination);
                        File.Move(temp, destination);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(temp);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        TryDelete(temp);
                        sink.Log("失败：" + ex.Message);
                    }
                }
            }
            throw new Exception("下载失败：" + url + Environment.NewLine + (lastError == null ? "" : lastError.Message));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void DownloadOnce(string url, string temp, IProgressSink sink, CancellationToken token)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", Program.UserAgent);
                var done = new ManualResetEventSlim(false);
                Exception error = null;
                bool cancelled = false;
                wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                {
                    sink.Progress(e.ProgressPercentage, 100);
                };
                wc.DownloadFileCompleted += delegate(object s, AsyncCompletedEventArgs e)
                {
                    if (e.Cancelled) cancelled = true;
                    else if (e.Error != null) error = e.Error;
                    done.Set();
                };
                using (token.Register(delegate { try { wc.CancelAsync(); } catch { } }))
                {
                    wc.DownloadFileAsync(new Uri(url), temp);
                    while (!done.Wait(100))
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { wc.CancelAsync(); } catch { }
                        }
                    }
                }
                if (cancelled || token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (error != null) throw error;
            }
        }

        public static string GetString(string url, string kind, IProgressSink sink)
        {
            Exception lastError = null;
            foreach (string candidate in Candidates(url, kind))
            {
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", Program.UserAgent);
                        wc.Headers.Add("Accept", "application/json");
                        wc.Encoding = Encoding.UTF8;
                        return wc.DownloadString(candidate);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (sink != null) sink.Log("读取失败：" + candidate + "（" + ex.Message + "）");
                }
            }
            throw new Exception("无法获取：" + url + Environment.NewLine + (lastError == null ? "" : lastError.Message));
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Deserialize<Dictionary<string, object>>(json);
        }

        public static Dictionary<string, object> Obj(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static object[] Arr(object value)
        {
            return value as object[];
        }

        public static string Str(object value)
        {
            if (value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static long Long(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch
            {
                double d;
                if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return (long)d;
                return 0;
            }
        }

        public static string SafeChild(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new Exception("文件路径为空。");
            if (Path.IsPathRooted(relative)) throw new Exception("拒绝绝对路径：" + relative);
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new Exception("路径超出所选目录：" + relative);
            return candidate;
        }

        public static bool WindowsRulesAllowed(Dictionary<string, object> lib)
        {
            if (!lib.ContainsKey("rules")) return true;
            object rawRules = lib["rules"];
            var rules = Arr(rawRules);
            if (rules == null) return true;
            bool allowed = false;
            bool is64 = Environment.Is64BitOperatingSystem;
            foreach (object rawRule in rules)
            {
                var rule = Obj(rawRule);
                if (rule == null) continue;
                bool match = true;
                var os = rule.ContainsKey("os") ? Obj(rule["os"]) : null;
                if (os != null)
                {
                    string name = os.ContainsKey("name") ? Str(os["name"]) : null;
                    if (!string.IsNullOrEmpty(name) && name != "windows") match = false;
                    string arch = os.ContainsKey("arch") ? Str(os["arch"]) : null;
                    if (!string.IsNullOrEmpty(arch))
                    {
                        if (arch == "x86" && is64) match = false;
                        if ((arch == "x86_64" || arch == "x64") && !is64) match = false;
                    }
                }
                if (rule.ContainsKey("features"))
                {
                    var features = Obj(rule["features"]);
                    if (features != null && features.Count > 0) match = false;
                }
                if (match) allowed = Str(rule.ContainsKey("action") ? rule["action"] : null) == "allow";
            }
            return allowed;
        }

        public static string NativeClassifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            return name.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
        }
    }

    internal sealed class MainForm : Form, IProgressSink
    {
        private readonly TextBox _gameRoot;
        private readonly TextBox _outputDir;
        private readonly ComboBox _versionBox;
        private readonly CheckBox _includeAssets;
        private readonly Button _loadVersions;
        private readonly Button _runVanilla;
        private readonly ComboBox _projectType;
        private readonly TextBox _searchBox;
        private readonly Button _searchButton;
        private readonly ListBox _results;
        private readonly TextBox _filterGameVersion;
        private readonly TextBox _filterLoader;
        private readonly Button _downloadProject;
        private readonly TextBox _directUrl;
        private readonly TextBox _directName;
        private readonly Button _directButton;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Button _cancelButton;
        private readonly TextBox _log;

        private readonly BackgroundWorker _worker;
        private CancellationTokenSource _cts;
        private List<VersionEntry> _versions = new List<VersionEntry>();
        private List<ProjectEntry> _projects = new List<ProjectEntry>();

        public MainForm()
        {
            Text = Program.AppName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 700);
            MinimumSize = new Size(780, 640);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 248, 252);

            var title = new Label();
            title.Text = "下载中心 · 官方文件校验 · 镜像自动重试";
            title.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(35, 93, 170);
            title.SetBounds(18, 12, 700, 30);
            Controls.Add(title);

            var rootLabel = new Label();
            rootLabel.Text = "Minecraft 游戏目录（应为包含 versions / libraries / assets 的实例根目录）";
            rootLabel.SetBounds(20, 48, 640, 20);
            Controls.Add(rootLabel);

            _gameRoot = new TextBox();
            _gameRoot.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            _gameRoot.SetBounds(20, 70, 700, 26);
            Controls.Add(_gameRoot);

            var browseRoot = new Button();
            browseRoot.Text = "选择…";
            browseRoot.SetBounds(730, 69, 120, 28);
            Controls.Add(browseRoot);
            browseRoot.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择 PCL 当前使用的游戏目录";
                    if (Directory.Exists(_gameRoot.Text)) dialog.SelectedPath = _gameRoot.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _gameRoot.Text = dialog.SelectedPath;
                }
            };

            var tabs = new TabControl();
            tabs.SetBounds(18, 108, 844, 420);
            Controls.Add(tabs);

            var vanillaTab = new TabPage("原版 Minecraft");
            tabs.TabPages.Add(vanillaTab);
            var vanillaInfo = new Label();
            vanillaInfo.Text = "读取 Mojang 版本清单，下载版本 JSON、客户端 JAR、Windows 库与原生库、日志配置，可选下载游戏资源。\r\n所有带 SHA-1 的文件都会校验；官方源失败时尝试 BMCLAPI。";
            vanillaInfo.SetBounds(16, 14, 790, 50);
            vanillaTab.Controls.Add(vanillaInfo);

            _loadVersions = new Button();
            _loadVersions.Text = "读取版本列表";
            _loadVersions.SetBounds(16, 76, 130, 30);
            vanillaTab.Controls.Add(_loadVersions);

            _versionBox = new ComboBox();
            _versionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _versionBox.SetBounds(160, 78, 420, 26);
            vanillaTab.Controls.Add(_versionBox);

            _includeAssets = new CheckBox();
            _includeAssets.Text = "同时下载游戏资源（完整启动通常需要，文件较多）";
            _includeAssets.Checked = true;
            _includeAssets.SetBounds(18, 124, 560, 24);
            vanillaTab.Controls.Add(_includeAssets);

            _runVanilla = new Button();
            _runVanilla.Text = "下载所选版本文件";
            _runVanilla.SetBounds(16, 168, 190, 38);
            vanillaTab.Controls.Add(_runVanilla);

            var vanillaNote = new Label();
            vanillaNote.Text = "说明：只补齐官方原版文件，不安装 Forge / Fabric / NeoForge，不启动游戏，不绕过正版验证。\r\n下载完成后在 PCL 中刷新版本列表。";
            vanillaNote.SetBounds(16, 220, 790, 46);
            vanillaTab.Controls.Add(vanillaNote);

            var modrinthTab = new TabPage("Modrinth / 整合包");
            tabs.TabPages.Add(modrinthTab);

            var typeLabel = new Label();
            typeLabel.Text = "类型";
            typeLabel.SetBounds(16, 18, 40, 22);
            modrinthTab.Controls.Add(typeLabel);

            _projectType = new ComboBox();
            _projectType.DropDownStyle = ComboBoxStyle.DropDownList;
            _projectType.Items.AddRange(new object[] { "整合包", "Mod", "资源包", "光影包" });
            _projectType.SelectedIndex = 0;
            _projectType.SetBounds(58, 14, 110, 26);
            modrinthTab.Controls.Add(_projectType);

            var keyLabel = new Label();
            keyLabel.Text = "关键词";
            keyLabel.SetBounds(186, 18, 50, 22);
            modrinthTab.Controls.Add(keyLabel);

            _searchBox = new TextBox();
            _searchBox.SetBounds(238, 14, 400, 26);
            modrinthTab.Controls.Add(_searchBox);

            _searchButton = new Button();
            _searchButton.Text = "搜索";
            _searchButton.SetBounds(650, 13, 110, 30);
            modrinthTab.Controls.Add(_searchButton);

            var resultLabel = new Label();
            resultLabel.Text = "搜索结果（选中后下载最新兼容文件）";
            resultLabel.SetBounds(16, 50, 400, 22);
            modrinthTab.Controls.Add(resultLabel);

            _results = new ListBox();
            _results.SetBounds(16, 74, 790, 186);
            _results.HorizontalScrollbar = true;
            modrinthTab.Controls.Add(_results);

            var gvLabel = new Label();
            gvLabel.Text = "游戏版本（可空）";
            gvLabel.SetBounds(16, 274, 130, 22);
            modrinthTab.Controls.Add(gvLabel);

            _filterGameVersion = new TextBox();
            _filterGameVersion.SetBounds(130, 270, 150, 26);
            modrinthTab.Controls.Add(_filterGameVersion);

            var loaderLabel = new Label();
            loaderLabel.Text = "加载器（fabric / forge / neoforge / quilt，可空）";
            loaderLabel.SetBounds(300, 274, 340, 22);
            modrinthTab.Controls.Add(loaderLabel);

            _filterLoader = new TextBox();
            _filterLoader.SetBounds(600, 270, 160, 26);
            modrinthTab.Controls.Add(_filterLoader);

            _downloadProject = new Button();
            _downloadProject.Text = "下载所选项目";
            _downloadProject.SetBounds(16, 314, 160, 36);
            modrinthTab.Controls.Add(_downloadProject);

            var modrinthNote = new Label();
            modrinthNote.Text = "整合包保存为 .mrpack；PCL 2.99 官方版已禁用整合包导入，本工具不绕过该限制。\r\nMod / 资源文件保存到下载目录，需按加载器手动放置。文件按 SHA-512 校验。";
            modrinthNote.SetBounds(190, 308, 616, 52);
            modrinthTab.Controls.Add(modrinthNote);

            var directTab = new TabPage("直接下载");
            tabs.TabPages.Add(directTab);

            var directInfo = new Label();
            directInfo.Text = "下载你信任来源的 HTTP/HTTPS 文件。本工具只保存文件，不运行它；HTTP 链接会提示风险。";
            directInfo.SetBounds(16, 18, 790, 42);
            directTab.Controls.Add(directInfo);

            var urlLabel = new Label();
            urlLabel.Text = "下载链接";
            urlLabel.SetBounds(16, 74, 90, 22);
            directTab.Controls.Add(urlLabel);

            _directUrl = new TextBox();
            _directUrl.SetBounds(108, 70, 688, 26);
            directTab.Controls.Add(_directUrl);

            var nameLabel = new Label();
            nameLabel.Text = "保存文件名（可空）";
            nameLabel.SetBounds(16, 114, 150, 22);
            directTab.Controls.Add(nameLabel);

            _directName = new TextBox();
            _directName.SetBounds(160, 110, 360, 26);
            directTab.Controls.Add(_directName);

            _directButton = new Button();
            _directButton.Text = "下载文件";
            _directButton.SetBounds(16, 158, 150, 36);
            directTab.Controls.Add(_directButton);

            var outputLabel = new Label();
            outputLabel.Text = "Modrinth / 直接下载保存到";
            outputLabel.SetBounds(20, 540, 260, 22);
            Controls.Add(outputLabel);

            _outputDir = new TextBox();
            _outputDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PCL-Download-Restore");
            _outputDir.SetBounds(20, 562, 700, 26);
            Controls.Add(_outputDir);

            var browseOutput = new Button();
            browseOutput.Text = "选择…";
            browseOutput.SetBounds(730, 561, 120, 28);
            Controls.Add(browseOutput);
            browseOutput.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择下载文件保存目录";
                    if (Directory.Exists(_outputDir.Text)) dialog.SelectedPath = _outputDir.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _outputDir.Text = dialog.SelectedPath;
                }
            };

            _status = new Label();
            _status.Text = "就绪。";
            _status.SetBounds(20, 596, 700, 22);
            Controls.Add(_status);

            _progress = new ProgressBar();
            _progress.SetBounds(20, 620, 700, 16);
            Controls.Add(_progress);

            _cancelButton = new Button();
            _cancelButton.Text = "取消";
            _cancelButton.Enabled = false;
            _cancelButton.SetBounds(730, 612, 120, 30);
            Controls.Add(_cancelButton);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font("Consolas", 8.5F);
            _log.SetBounds(20, 644, 830, 46);
            Controls.Add(_log);

            AcceptButton = null;

            _worker = new BackgroundWorker();
            _worker.WorkerReportsProgress = true;
            _worker.WorkerSupportsCancellation = true;
            _worker.DoWork += WorkerDoWork;
            _worker.ProgressChanged += WorkerProgressChanged;
            _worker.RunWorkerCompleted += WorkerCompleted;

            _loadVersions.Click += delegate { StartJob(JobKind.LoadVersions); };
            _runVanilla.Click += delegate { StartJob(JobKind.Vanilla); };
            _searchButton.Click += delegate { StartJob(JobKind.Search); };
            _downloadProject.Click += delegate { StartJob(JobKind.Project); };
            _directButton.Click += delegate { StartJob(JobKind.Direct); };
            _cancelButton.Click += delegate
            {
                if (_cts != null) _cts.Cancel();
                _status.Text = "正在取消…";
            };
            _searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !_worker.IsBusy) { StartJob(JobKind.Search); e.SuppressKeyPress = true; }
            };

            AppendLog("PCL 下载恢复工具已启动（v1.1.0）。");
            AppendLog("本程序不启动游戏、不执行下载内容，也不需要 Microsoft/Mojang 登录令牌。");
            AppendLog("原版下载会写入所选游戏目录，建议先备份现有实例。");
        }

        private enum JobKind
        {
            LoadVersions,
            Vanilla,
            Search,
            Project,
            Direct
        }

        private JobKind _job;
        private object _jobArgument;

        private void StartJob(JobKind kind)
        {
            if (_worker.IsBusy) return;
            try
            {
                _jobArgument = PrepareJob(kind);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _job = kind;
            _cts = new CancellationTokenSource();
            _cancelButton.Enabled = true;
            SetBusy(true);
            _worker.RunWorkerAsync();
        }

        private object PrepareJob(JobKind kind)
        {
            switch (kind)
            {
                case JobKind.Vanilla:
                    if (_versionBox.SelectedIndex < 0 || _versionBox.SelectedIndex >= _versions.Count)
                        throw new Exception("请先读取并选择 Minecraft 版本。");
                    RequireDirectory(_gameRoot.Text);
                    return _versions[_versionBox.SelectedIndex];
                case JobKind.Project:
                    if (_results.SelectedIndex < 0 || _results.SelectedIndex >= _projects.Count)
                        throw new Exception("请先选择一个搜索结果。");
                    RequireDirectory(_outputDir.Text);
                    return _projects[_results.SelectedIndex];
                case JobKind.Direct:
                    var url = _directUrl.Text.Trim();
                    Uri uri;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                        throw new Exception("请输入完整的 HTTP 或 HTTPS 下载链接。");
                    if (uri.Scheme == "http")
                    {
                        if (MessageBox.Show(this, "该链接未加密（HTTP），下载内容可能被篡改。仍要继续吗？", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                            throw new Exception("已取消。");
                    }
                    RequireDirectory(_outputDir.Text);
                    return uri;
                default:
                    return null;
            }
        }

        private static void RequireDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new Exception("请先选择目录。");
            Directory.CreateDirectory(path);
        }

        private void SetBusy(bool busy)
        {
            _loadVersions.Enabled = !busy;
            _runVanilla.Enabled = !busy;
            _searchButton.Enabled = !busy;
            _downloadProject.Enabled = !busy;
            _directButton.Enabled = !busy;
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            var token = _cts.Token;
            try
            {
                switch (_job)
                {
                    case JobKind.LoadVersions: DoLoadVersions(token); break;
                    case JobKind.Vanilla: DoVanilla((VersionEntry)_jobArgument, token); break;
                    case JobKind.Search: DoSearch(token); break;
                    case JobKind.Project: DoProject((ProjectEntry)_jobArgument, token); break;
                    case JobKind.Direct: DoDirect((Uri)_jobArgument, token); break;
                }
            }
            catch (OperationCanceledException)
            {
                e.Result = "已取消。";
            }
            catch (Exception ex)
            {
                e.Result = ex;
            }
        }

        private void WorkerProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            var message = e.UserState as string;
            if (message == null) return;
            if (message.StartsWith("LOG:"))
            {
                AppendLog(message.Substring(4));
            }
            else if (message.StartsWith("STATUS:"))
            {
                _status.Text = message.Substring(7);
            }
            else if (message.StartsWith("PROGRESS:"))
            {
                string[] parts = message.Substring(9).Split('/');
                int current;
                int total;
                if (parts.Length == 2 && int.TryParse(parts[0], out current) && int.TryParse(parts[1], out total))
                {
                    SetProgressBar(current, total);
                }
            }
        }

        private void SetProgressBar(int current, int total)
        {
            if (total <= 0)
            {
                _progress.Style = ProgressBarStyle.Marquee;
                return;
            }
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Maximum = Math.Max(1, total);
            _progress.Value = Math.Max(0, Math.Min(current, _progress.Maximum));
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            _cancelButton.Enabled = false;
            SetBusy(false);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            if (e.Error != null)
            {
                AppendLog("操作异常：" + e.Error.Message);
                MessageBox.Show(this, e.Error.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (e.Result is Exception)
            {
                var ex = (Exception)e.Result;
                AppendLog("操作失败：" + ex.Message);
                MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var text = e.Result as string;
            if (!string.IsNullOrEmpty(text))
            {
                AppendLog(text);
                _status.Text = text;
            }
        }

        private void Report(string text)
        {
            _worker.ReportProgress(0, "STATUS:" + text);
        }

        private void LogLine(string text)
        {
            _worker.ReportProgress(0, "LOG:" + text);
        }

        private void ReportProgressCount(int current, int total)
        {
            _worker.ReportProgress(0, "PROGRESS:" + current.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture));
        }

        void IProgressSink.Log(string text) { LogLine(text); }
        void IProgressSink.Status(string text) { Report(text); }
        void IProgressSink.Progress(int current, int total) { ReportProgressCount(current, total); }

        private void AppendLog(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), text);
                return;
            }
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + text + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void DoLoadVersions(CancellationToken token)
        {
            Report("正在读取 Mojang 版本清单…");
            ReportProgressCount(0, 0);
            string json = Engine.GetString("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "meta", this);
            var manifest = Engine.ParseObject(json);
            var versions = new List<VersionEntry>();
            var rawVersions = Engine.Arr(manifest.ContainsKey("versions") ? manifest["versions"] : null);
            if (rawVersions != null)
            {
                foreach (object raw in rawVersions)
                {
                    token.ThrowIfCancellationRequested();
                    var item = Engine.Obj(raw);
                    if (item == null) continue;
                    versions.Add(new VersionEntry
                    {
                        Id = Engine.Str(item.ContainsKey("id") ? item["id"] : null),
                        Type = Engine.Str(item.ContainsKey("type") ? item["type"] : null),
                        Url = Engine.Str(item.ContainsKey("url") ? item["url"] : null),
                        Sha1 = Engine.Str(item.ContainsKey("sha1") ? item["sha1"] : null)
                    });
                }
            }
            if (versions.Count == 0) throw new Exception("版本清单为空，可能被网络拦截。");
            _versions = versions;
            string latest = null;
            var latestObj = manifest.ContainsKey("latest") ? Engine.Obj(manifest["latest"]) : null;
            if (latestObj != null) latest = Engine.Str(latestObj.ContainsKey("release") ? latestObj["release"] : null);
            string capturedLatest = latest;
            BeginInvoke(new Action(delegate
            {
                _versionBox.Items.Clear();
                foreach (VersionEntry entry in _versions) _versionBox.Items.Add(entry);
                for (int i = 0; i < _versions.Count; i++)
                {
                    if (_versions[i].Id == capturedLatest) { _versionBox.SelectedIndex = i; break; }
                }
            }));
            ReportProgressCount(1, 1);
            Report(string.Format("已载入 {0} 个版本，最新正式版：{1}", versions.Count, latest));
            LogLine("版本清单载入完成。");
        }

        private void DoVanilla(VersionEntry entry, CancellationToken token)
        {
            string root = _gameRoot.Text;
            string id = entry.Id;
            if (string.IsNullOrEmpty(id)) throw new Exception("版本 ID 无效。");
            foreach (char c in id)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    throw new Exception("版本 ID 含非法字符：" + id);
            }
            string versionDir = Path.Combine(Path.Combine(root, "versions"), id);
            Directory.CreateDirectory(versionDir);
            string jsonPath = Path.Combine(versionDir, id + ".json");
            string jarPath = Path.Combine(versionDir, id + ".jar");

            Report("获取 " + id + " 的版本元数据…");
            Engine.Download(entry.Url, jsonPath, entry.Sha1, "SHA1", 0, "meta", this, token);
            var meta = Engine.ParseObject(File.ReadAllText(jsonPath, Encoding.UTF8));
            string metaId = Engine.Str(meta.ContainsKey("id") ? meta["id"] : null);
            if (metaId != id) throw new Exception("版本元数据 ID 不匹配：" + metaId);

            var downloads = meta.ContainsKey("downloads") ? Engine.Obj(meta["downloads"]) : null;
            var client = downloads != null && downloads.ContainsKey("client") ? Engine.Obj(downloads["client"]) : null;
            if (client == null) throw new Exception("该版本没有官方客户端 JAR 下载信息。");
            Report("下载 " + id + " 客户端 JAR…");
            Engine.Download(Engine.Str(client["url"]), jarPath, Engine.Str(client["sha1"]), "SHA1", Engine.Long(client["size"]), "meta", this, token);

            var tasks = new List<DownloadTask>();
            var libraries = meta.ContainsKey("libraries") ? Engine.Arr(meta["libraries"]) : null;
            if (libraries != null)
            {
                foreach (object rawLib in libraries)
                {
                    token.ThrowIfCancellationRequested();
                    var lib = Engine.Obj(rawLib);
                    if (lib == null) continue;
                    if (!Engine.WindowsRulesAllowed(lib)) continue;
                    var libDownloads = lib.ContainsKey("downloads") ? Engine.Obj(lib["downloads"]) : null;
                    if (libDownloads == null) continue;
                    var artifact = libDownloads.ContainsKey("artifact") ? Engine.Obj(libDownloads["artifact"]) : null;
                    if (artifact != null)
                    {
                        string path = Engine.Str(artifact.ContainsKey("path") ? artifact["path"] : null);
                        string url = Engine.Str(artifact.ContainsKey("url") ? artifact["url"] : null);
                        if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(url))
                        {
                            tasks.Add(new DownloadTask
                            {
                                Url = url, Path = Engine.SafeChild(root, path),
                                Hash = Engine.Str(artifact.ContainsKey("sha1") ? artifact["sha1"] : null),
                                Size = Engine.Long(artifact.ContainsKey("size") ? artifact["size"] : null),
                                Algo = "SHA1", Kind = "library",
                                Label = Engine.Str(lib.ContainsKey("name") ? lib["name"] : null)
                            });
                        }
                    }
                    var natives = lib.ContainsKey("natives") ? Engine.Obj(lib["natives"]) : null;
                    if (natives != null && natives.ContainsKey("windows"))
                    {
                        string classifierName = Engine.NativeClassifier(Engine.Str(natives["windows"]));
                        var classifiers = libDownloads.ContainsKey("classifiers") ? Engine.Obj(libDownloads["classifiers"]) : null;
                        if (classifiers != null && classifiers.ContainsKey(classifierName))
                        {
                            var classifier = Engine.Obj(classifiers[classifierName]);
                            if (classifier != null)
                            {
                                string path = Engine.Str(classifier.ContainsKey("path") ? classifier["path"] : null);
                                string url = Engine.Str(classifier.ContainsKey("url") ? classifier["url"] : null);
                                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(url))
                                {
                                    tasks.Add(new DownloadTask
                                    {
                                        Url = url, Path = Engine.SafeChild(root, path),
                                        Hash = Engine.Str(classifier.ContainsKey("sha1") ? classifier["sha1"] : null),
                                        Size = Engine.Long(classifier.ContainsKey("size") ? classifier["size"] : null),
                                        Algo = "SHA1", Kind = "library",
                                        Label = Engine.Str(lib.ContainsKey("name") ? lib["name"] : null) + " [" + classifierName + "]"
                                    });
                                }
                            }
                        }
                    }
                }
            }

            if (_includeAssets.Checked)
            {
                var assetIndex = meta.ContainsKey("assetIndex") ? Engine.Obj(meta["assetIndex"]) : null;
                if (assetIndex != null)
                {
                    string indexId = Engine.Str(assetIndex.ContainsKey("id") ? assetIndex["id"] : null);
                    string indexUrl = Engine.Str(assetIndex.ContainsKey("url") ? assetIndex["url"] : null);
                    if (!string.IsNullOrEmpty(indexId) && !string.IsNullOrEmpty(indexUrl))
                    {
                        string indexFile = Engine.SafeChild(root, "assets/indexes/" + indexId + ".json");
                        Report("下载资源索引 " + indexId + "…");
                        Engine.Download(indexUrl, indexFile, Engine.Str(assetIndex["sha1"]), "SHA1", Engine.Long(assetIndex["size"]), "meta", this, token);
                        var index = Engine.ParseObject(File.ReadAllText(indexFile, Encoding.UTF8));
                        var objects = index.ContainsKey("objects") ? Engine.Obj(index["objects"]) : null;
                        if (objects != null)
                        {
                            foreach (KeyValuePair<string, object> pair in objects)
                            {
                                token.ThrowIfCancellationRequested();
                                var obj = Engine.Obj(pair.Value);
                                if (obj == null) continue;
                                string hash = Engine.Str(obj.ContainsKey("hash") ? obj["hash"] : null);
                                if (string.IsNullOrEmpty(hash) || hash.Length != 40) throw new Exception("资源索引包含无效 SHA-1：" + pair.Key);
                                string prefix = hash.Substring(0, 2);
                                tasks.Add(new DownloadTask
                                {
                                    Url = "https://resources.download.minecraft.net/" + prefix + "/" + hash,
                                    Path = Engine.SafeChild(root, "assets/objects/" + prefix + "/" + hash),
                                    Hash = hash, Algo = "SHA1",
                                    Size = Engine.Long(obj.ContainsKey("size") ? obj["size"] : null),
                                    Kind = "asset", Label = pair.Key
                                });
                            }
                        }
                    }
                }
            }

            var logging = meta.ContainsKey("logging") ? Engine.Obj(meta["logging"]) : null;
            var loggingClient = logging != null && logging.ContainsKey("client") ? Engine.Obj(logging["client"]) : null;
            var loggingFile = loggingClient != null && loggingClient.ContainsKey("file") ? Engine.Obj(loggingClient["file"]) : null;
            if (loggingFile != null)
            {
                string logId = Engine.Str(loggingFile.ContainsKey("id") ? loggingFile["id"] : null);
                string logUrl = Engine.Str(loggingFile.ContainsKey("url") ? loggingFile["url"] : null);
                if (!string.IsNullOrEmpty(logId) && !string.IsNullOrEmpty(logUrl))
                {
                    tasks.Add(new DownloadTask
                    {
                        Url = logUrl, Path = Engine.SafeChild(root, "assets/log_configs/" + logId),
                        Hash = Engine.Str(loggingFile.ContainsKey("sha1") ? loggingFile["sha1"] : null),
                        Size = Engine.Long(loggingFile.ContainsKey("size") ? loggingFile["size"] : null),
                        Algo = "SHA1", Kind = "meta", Label = "logging config"
                    });
                }
            }

            int total = tasks.Count;
            int done = 0;
            foreach (DownloadTask task in tasks)
            {
                token.ThrowIfCancellationRequested();
                done++;
                Report(string.Format("下载依赖与资源：{0} / {1} — {2}", done, total, task.Label));
                ReportProgressCount(done, total);
                Engine.Download(task.Url, task.Path, task.Hash, task.Algo, task.Size, task.Kind, this, token);
                if (done % 20 == 0) LogLine(string.Format("已完成 {0} / {1} 个文件。", done, total));
            }
            ReportProgressCount(total, Math.Max(1, total));
            LogLine("完成：" + id + "；根目录：" + root);
            MessageBox.Show(this,
                "版本 " + id + " 的官方文件已下载到：" + Environment.NewLine + root + Environment.NewLine + Environment.NewLine +
                "本工具不会启动游戏。请在 PCL 中刷新版本列表；首次启动仍需正版账号验证。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Report("版本 " + id + " 已下载完成。");
        }

        private string ProjectTypeValue()
        {
            switch (Convert.ToString(_projectType.SelectedItem))
            {
                case "Mod": return "mod";
                case "资源包": return "resourcepack";
                case "光影包": return "shader";
                default: return "modpack";
            }
        }

        private void DoSearch(CancellationToken token)
        {
            string query = _searchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) throw new Exception("请输入搜索关键词。");
            string type = ProjectTypeValue();
            string url = "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(query) +
                         "&facets=" + Uri.EscapeDataString("[[\"project_type:" + type + "\"]]") +
                         "&limit=30&index=relevance";
            Report("正在 Modrinth 搜索 " + type + "…");
            string json = Engine.GetString(url, "direct", this);
            var response = Engine.ParseObject(json);
            var hits = Engine.Arr(response.ContainsKey("hits") ? response["hits"] : null);
            var projects = new List<ProjectEntry>();
            if (hits != null)
            {
                foreach (object raw in hits)
                {
                    token.ThrowIfCancellationRequested();
                    var item = Engine.Obj(raw);
                    if (item == null) continue;
                    projects.Add(new ProjectEntry
                    {
                        Id = Engine.Str(item.ContainsKey("project_id") ? item["project_id"] : null),
                        Title = Engine.Str(item.ContainsKey("title") ? item["title"] : null),
                        Description = Engine.Str(item.ContainsKey("description") ? item["description"] : null)
                    });
                }
            }
            _projects = projects;
            BeginInvoke(new Action(delegate
            {
                _results.Items.Clear();
                foreach (ProjectEntry project in _projects) _results.Items.Add(project);
            }));
            Report(string.Format("找到 {0} 个项目。", projects.Count));
            LogLine("Modrinth 搜索完成。");
        }

        private void DoProject(ProjectEntry project, CancellationToken token)
        {
            string type = ProjectTypeValue();
            string versionUrl = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(project.Id) + "/version";
            Report("读取 " + project.Title + " 的可用版本…");
            string json = Engine.GetString(versionUrl, "direct", this);
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            var versionList = serializer.Deserialize<object[]>(json);
            string filterGame = _filterGameVersion.Text.Trim();
            string filterLoader = _filterLoader.Text.Trim().ToLowerInvariant();
            Dictionary<string, object> chosen = null;
            if (versionList != null)
            {
                foreach (object rawVersion in versionList)
                {
                    token.ThrowIfCancellationRequested();
                    var version = Engine.Obj(rawVersion);
                    if (version == null) continue;
                    if (!string.IsNullOrEmpty(filterGame) && !StringArrayContains(version, "game_versions", filterGame)) continue;
                    if (!string.IsNullOrEmpty(filterLoader) && !StringArrayContains(version, "loaders", filterLoader)) continue;
                    chosen = version;
                    break;
                }
            }
            if (chosen == null) throw new Exception("没有找到符合条件的发布版本。清空筛选后重试。");

            var files = Engine.Arr(chosen.ContainsKey("files") ? chosen["files"] : null);
            Dictionary<string, object> file = null;
            if (files != null)
            {
                foreach (object rawFile in files)
                {
                    var candidate = Engine.Obj(rawFile);
                    if (candidate == null) continue;
                    object primary;
                    bool isPrimary = candidate.TryGetValue("primary", out primary) && Convert.ToBoolean(primary, CultureInfo.InvariantCulture);
                    if (file == null || (isPrimary && !PrimaryFlag(file))) file = candidate;
                    if (isPrimary) break;
                }
            }
            if (file == null) throw new Exception("所选发布版本没有可下载文件。");
            string fileUrl = Engine.Str(file.ContainsKey("url") ? file["url"] : null);
            string fileName = Path.GetFileName(Engine.Str(file.ContainsKey("filename") ? file["filename"] : null));
            if (string.IsNullOrEmpty(fileUrl) || string.IsNullOrEmpty(fileName)) throw new Exception("Modrinth 返回的文件信息无效。");
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Exception("文件名无效。");

            string folder = _outputDir.Text;
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, fileName);
            if (File.Exists(destination))
            {
                destination = Path.Combine(folder,
                    Path.GetFileNameWithoutExtension(fileName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(fileName));
            }
            string sha512 = null;
            var hashes = file.ContainsKey("hashes") ? Engine.Obj(file["hashes"]) : null;
            if (hashes != null) sha512 = Engine.Str(hashes.ContainsKey("sha512") ? hashes["sha512"] : null);
            if (string.IsNullOrEmpty(sha512) || sha512.Length != 128) throw new Exception("Modrinth 未返回可用 SHA-512，拒绝下载。");

            Report("下载 Modrinth 文件：" + fileName);
            Engine.Download(fileUrl, destination, sha512, "SHA512", Engine.Long(file.ContainsKey("size") ? file["size"] : null), "modrinth", this, token);
            LogLine("已保存：" + destination);
            if (type == "modpack")
            {
                MessageBox.Show(this,
                    "整合包已保存：" + Environment.NewLine + destination + Environment.NewLine + Environment.NewLine +
                    "注意：PCL 2.99 官方版禁用了整合包导入。本工具只下载文件，不绕过该限制。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this,
                    "文件已保存并通过 SHA-512 校验：" + Environment.NewLine + destination + Environment.NewLine + Environment.NewLine +
                    "请按对应加载器/资源类型手动放入实例目录。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Report("下载完成。");
        }

        private static bool PrimaryFlag(Dictionary<string, object> file)
        {
            object value;
            if (!file.TryGetValue("primary", out value) || value == null) return false;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private static bool StringArrayContains(Dictionary<string, object> obj, string key, string expected)
        {
            if (!obj.ContainsKey(key)) return false;
            var values = Engine.Arr(obj[key]);
            if (values == null) return false;
            foreach (object raw in values)
            {
                if (string.Equals(Engine.Str(raw), expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void DoDirect(Uri uri, CancellationToken token)
        {
            string folder = _outputDir.Text;
            Directory.CreateDirectory(folder);
            string name = _directName.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new Exception("文件名无效，请填写简单文件名。");
            string destination = Path.Combine(folder, name);
            if (File.Exists(destination))
            {
                destination = Path.Combine(folder,
                    Path.GetFileNameWithoutExtension(name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(name));
            }
            Report("下载文件：" + uri.Host);
            Engine.Download(uri.AbsoluteUri, destination, null, "SHA256", 0, "direct", this, token);
            LogLine("已保存：" + destination);
            MessageBox.Show(this, "下载完成：" + Environment.NewLine + destination, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Report("下载完成。");
        }
    }
}
