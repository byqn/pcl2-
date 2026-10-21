// PCL 下载恢复工具 v1.7.0
// 编译：csc /target:winexe /codepage:65001 /out:dist\PCLDownloadRestore.exe src\PCLDownloadRestore.cs
// 目标框架：.NET Framework 3.5+（Windows 10/11 自带 4.8 可直接运行）
// 本程序只下载文件，不启动游戏、不执行下载内容、不修改 PCL。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

namespace PclDownloadRestore
{
    internal static class Program
    {
        internal const string AppName = "PCL 下载恢复工具";
        internal const string UserAgent = "PCL-Download-Restore/1.5.1";

        [STAThread]
        private static void Main()
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            catch { }
            ServicePointManager.Expect100Continue = false;
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && string.Equals(args[1], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                RunSelfTest();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void RunSelfTest()
        {
            var lines = new List<string>();
            try
            {
                var versions = Engine.FetchManifest(null);
                lines.Add("VERSIONS=" + versions.Count.ToString(CultureInfo.InvariantCulture));
                if (versions.Count > 0) lines.Add("FIRST=" + versions[0].Id + " " + versions[0].Url);
                var modrinth = Engine.GetString("https://api.modrinth.com/v2/search?query=sodium&limit=3", "direct", null);
                var parsed = Engine.ParseObject(modrinth);
                var hits = Engine.Arr(parsed.ContainsKey("hits") ? parsed["hits"] : null);
                lines.Add("MODRINTH_HITS=" + (hits == null ? -1 : hits.Length));
                if (hits != null && hits.Length > 0)
                {
                    var first = Engine.Obj(hits[0]);
                    lines.Add("MODRINTH_FIRST=" + (first != null && first.ContainsKey("title") ? Engine.Str(first["title"]) : "?"));
                }
                lines.Add("RESULT=OK");
            }
            catch (Exception ex)
            {
                lines.Add("RESULT=FAIL");
                lines.Add("ERROR=" + ex.Message);
            }
            try
            {
                File.WriteAllLines(Path.Combine(Application.StartupPath, "selftest.log"), lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
            Environment.Exit(0);
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
        public string TypeLabel { get; set; }
        public string Source { get; set; }
        public string Slug { get; set; }
        public override string ToString()
        {
            string prefix = string.IsNullOrEmpty(TypeLabel) ? "" : "[" + TypeLabel + "] ";
            string tag = Source == "curseforge" ? "  · CF" : "";
            return prefix + Title + tag + "  —  " + Description;
        }
    }

    internal sealed class GameRequest
    {
        public string McVersion { get; set; }
        public string LoaderType { get; set; }
        public string LoaderVersion { get; set; }
        public string OptiFineType { get; set; }
        public string OptiFinePatch { get; set; }
    }

    internal sealed class LoaderEntry
    {
        public string Type { get; set; }
        public string McVersion { get; set; }
        public string Version { get; set; }
        public string ExtraType { get; set; }
        public string ExtraPatch { get; set; }
        public string InstallerUrl { get; set; }

        public override string ToString()
        {
            if (Type == "optifine")
                return "OptiFine " + ExtraType + " " + ExtraPatch + "  (" + McVersion + ")";
            return Version + "  (" + McVersion + ")";
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

        public static IWebProxy Proxy = null;

        public static void SetProxy(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                Proxy = null;
                return;
            }
            try { Proxy = new WebProxy(address.Trim()); }
            catch { Proxy = null; }
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

            string part = destination + ".part";
            Exception lastError = null;
            foreach (string candidate in Candidates(url, kind))
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    try
                    {
                        long resumeFrom = 0;
                        if (File.Exists(part))
                        {
                            resumeFrom = new FileInfo(part).Length;
                            if (expectedSize > 0 && resumeFrom >= expectedSize)
                            {
                                TryDelete(part);
                                resumeFrom = 0;
                            }
                        }
                        sink.Log(string.Format("下载尝试 {0}/3：{1}{2}", attempt, candidate,
                            resumeFrom > 0 ? string.Format("（从 {0} 字节续传）", resumeFrom) : ""));
                        DownloadOnce(candidate, part, resumeFrom, sink, token);
                        if (!File.Exists(part)) throw new Exception("服务端未生成下载文件。");
                        long length = new FileInfo(part).Length;
                        if (expectedSize > 0 && length != expectedSize)
                            throw new Exception(string.Format("文件大小不符：预期 {0} 字节，实际 {1} 字节。", expectedSize, length));
                        if (!string.IsNullOrEmpty(expectedHash))
                        {
                            string actual = HashFile(part, algo);
                            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                            {
                                TryDelete(part);
                                throw new Exception(algo + " 校验失败，已删除损坏的部分文件。");
                            }
                        }
                        if (File.Exists(destination)) File.Delete(destination);
                        File.Move(part, destination);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        sink.Log("失败：" + ex.Message);
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    }
                }
            }
            throw new Exception("下载失败：" + url + Environment.NewLine + (lastError == null ? "" : lastError.Message));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void DownloadOnce(string url, string partPath, long resumeFrom, IProgressSink sink, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = Program.UserAgent;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            if (Proxy != null) request.Proxy = Proxy;
            if (resumeFrom > 0) request.AddRange(resumeFrom);

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                bool append = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                long baseOffset = append ? resumeFrom : 0;
                long total = response.ContentLength > 0 ? baseOffset + response.ContentLength : 0;
                using (var stream = response.GetResponseStream())
                using (var file = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    byte[] buffer = new byte[81920];
                    long received = baseOffset;
                    int lastBucket = -1;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        file.Write(buffer, 0, read);
                        received += read;
                        if (total > 0)
                        {
                            int percent = (int)(received * 100 / total);
                            int bucket = percent / 10;
                            if (bucket != lastBucket)
                            {
                                lastBucket = bucket;
                                sink.Progress(percent, 100);
                            }
                        }
                    }
                }
            }
        }

        public static string GetString(string url, string kind, IProgressSink sink)
        {
            Exception lastError = null;
            foreach (string candidate in Candidates(url, kind))
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(candidate);
                    request.UserAgent = Program.UserAgent;
                    request.Accept = "application/json";
                    request.Timeout = 30000;
                    request.ReadWriteTimeout = 60000;
                    if (Proxy != null) request.Proxy = Proxy;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
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

        public static string CfKey = null;

        public static void SetCfKey(string key)
        {
            CfKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        public static string GetCurseForgeJson(string url)
        {
            if (string.IsNullOrEmpty(CfKey)) throw new Exception("未设置 CurseForge API Key。");
            using (var wc = new WebClient())
            {
                wc.Headers.Add("x-api-key", CfKey);
                wc.Headers.Add("Accept", "application/json");
                wc.Headers.Add("User-Agent", Program.UserAgent);
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString(url);
            }
        }

        public static List<VersionEntry> FetchManifest(IProgressSink sink)
        {
            string json = GetString("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "meta", sink);
            var manifest = ParseObject(json);
            var result = new List<VersionEntry>();
            var rawVersions = Arr(manifest.ContainsKey("versions") ? manifest["versions"] : null);
            if (rawVersions == null) return result;
            foreach (object raw in rawVersions)
            {
                var item = Obj(raw);
                if (item == null) continue;
                result.Add(new VersionEntry
                {
                    Id = Str(item.ContainsKey("id") ? item["id"] : null),
                    Type = Str(item.ContainsKey("type") ? item["type"] : null),
                    Url = Str(item.ContainsKey("url") ? item["url"] : null),
                    Sha1 = Str(item.ContainsKey("sha1") ? item["sha1"] : null)
                });
            }
            return result;
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Deserialize<Dictionary<string, object>>(json);
        }

        public static object[] ParseArray(string json)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Deserialize<object[]>(json);
        }

        public static Dictionary<string, object> Obj(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static object[] Arr(object value)
        {
            var array = value as object[];
            if (array != null) return array;
            var list = value as System.Collections.ArrayList;
            if (list != null) return list.ToArray();
            return null;
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
        private readonly TextBox _proxyBox;
        private readonly TextBox _cfKeyBox;
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
        private readonly Button _installModpack;
        private readonly TextBox _directUrl;
        private readonly TextBox _directName;
        private readonly Button _directButton;
        private readonly ComboBox _loaderType;
        private readonly TextBox _loaderMc;
        private readonly Button _loadLoaderBtn;
        private readonly ListBox _loaderList;
        private readonly Button _installLoaderBtn;
        private readonly Button _quickInstallBtn;
        private readonly ComboBox _dlMcBox;
        private readonly ComboBox _dlLoaderBox;
        private readonly ComboBox _dlLoaderVersionBox;
        private readonly Button _dlStartBtn;
        private string _bgImagePath;
        private readonly TrackBar _opacitySlider;
        private readonly Label _opacityLabel;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Button _cancelButton;
        private readonly TextBox _log;

        private readonly BackgroundWorker _worker;
        private CancellationTokenSource _cts;
        private List<VersionEntry> _versions = new List<VersionEntry>();
        private List<ProjectEntry> _projects = new List<ProjectEntry>();
        private List<LoaderEntry> _loaderEntries = new List<LoaderEntry>();

        public MainForm()
        {
            Text = Program.AppName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 790);
            MinimumSize = new Size(780, 640);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 248, 252);

            var title = new Label();
            title.Text = "下载中心";
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(30, 80, 150);
            title.BackColor = Color.Transparent;
            title.SetBounds(18, 12, 320, 34);
            Controls.Add(title);

            var bgButton = new Button();
            bgButton.Text = "换背景";
            bgButton.SetBounds(748, 15, 110, 30);
            Controls.Add(bgButton);
            bgButton.Click += delegate { ChangeBackground(); };

            _opacityLabel = new Label();
            _opacityLabel.Text = "100%";
            _opacityLabel.BackColor = Color.Transparent;
            _opacityLabel.ForeColor = Color.FromArgb(60, 80, 110);
            _opacityLabel.SetBounds(500, 18, 50, 22);
            Controls.Add(_opacityLabel);

            _opacitySlider = new TrackBar();
            _opacitySlider.Minimum = 30;
            _opacitySlider.Maximum = 100;
            _opacitySlider.TickFrequency = 10;
            _opacitySlider.Value = 100;
            _opacitySlider.SetBounds(548, 10, 180, 40);
            Controls.Add(_opacitySlider);
            _opacitySlider.Scroll += delegate { ApplyOpacity(_opacitySlider.Value); };

            var rootLabel = new Label();
            rootLabel.Text = "Minecraft 游戏目录（应为包含 versions / libraries / assets 的实例根目录）";
            rootLabel.SetBounds(20, 48, 640, 20);
            Controls.Add(rootLabel);

            _gameRoot = new TextBox();
            _gameRoot.Text = DetectGameRoot();
            _gameRoot.SetBounds(20, 70, 600, 26);
            Controls.Add(_gameRoot);

            var autoDetectBtn = new Button();
            autoDetectBtn.Text = "自动查找";
            autoDetectBtn.SetBounds(628, 69, 100, 28);
            Controls.Add(autoDetectBtn);
            autoDetectBtn.Click += delegate { _gameRoot.Text = DetectGameRoot(); };

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
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.ItemSize = new Size(112, 34);
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.BackColor = Color.White;
            tabs.DrawItem += DrawTabItem;
            Controls.Add(tabs);

            var downloadTab = new TabPage("下载游戏");
            tabs.TabPages.Add(downloadTab);

            var dlTitle = new Label();
            dlTitle.Text = "选择 Minecraft 版本和加载器，然后点「开始下载」";
            dlTitle.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            dlTitle.SetBounds(20, 16, 500, 26);
            downloadTab.Controls.Add(dlTitle);

            var dlMcLabel = new Label();
            dlMcLabel.Text = "Minecraft 版本";
            dlMcLabel.SetBounds(20, 64, 90, 24);
            downloadTab.Controls.Add(dlMcLabel);

            _dlMcBox = new ComboBox();
            _dlMcBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _dlMcBox.SetBounds(116, 60, 260, 28);
            downloadTab.Controls.Add(_dlMcBox);

            var dlRefreshMc = new Button();
            dlRefreshMc.Text = "刷新";
            dlRefreshMc.SetBounds(386, 59, 80, 30);
            downloadTab.Controls.Add(dlRefreshMc);

            var dlLoaderLabel = new Label();
            dlLoaderLabel.Text = "加载器";
            dlLoaderLabel.SetBounds(20, 112, 90, 24);
            downloadTab.Controls.Add(dlLoaderLabel);

            _dlLoaderBox = new ComboBox();
            _dlLoaderBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _dlLoaderBox.Items.AddRange(new object[] { "无（仅原版）", "Fabric", "Forge", "NeoForge", "OptiFine" });
            _dlLoaderBox.SelectedIndex = 0;
            _dlLoaderBox.SetBounds(116, 108, 260, 28);
            downloadTab.Controls.Add(_dlLoaderBox);

            var dlLoaderVersionLabel = new Label();
            dlLoaderVersionLabel.Text = "加载器版本";
            dlLoaderVersionLabel.SetBounds(20, 160, 90, 24);
            downloadTab.Controls.Add(dlLoaderVersionLabel);

            _dlLoaderVersionBox = new ComboBox();
            _dlLoaderVersionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _dlLoaderVersionBox.SetBounds(116, 156, 350, 28);
            downloadTab.Controls.Add(_dlLoaderVersionBox);

            var dlRefreshLoader = new Button();
            dlRefreshLoader.Text = "刷新版本";
            dlRefreshLoader.SetBounds(476, 155, 90, 30);
            downloadTab.Controls.Add(dlRefreshLoader);

            _dlStartBtn = new Button();
            _dlStartBtn.Text = "开始下载";
            _dlStartBtn.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            _dlStartBtn.SetBounds(116, 210, 260, 46);
            downloadTab.Controls.Add(_dlStartBtn);

            var dlHint = new Label();
            dlHint.Text = "Forge / NeoForge 需要本机安装 Java（17 或更高）；OptiFine 只下载官方安装器，需手动双击运行。\r\n加载器版本留空会自动选最新。";
            dlHint.SetBounds(20, 274, 790, 50);
            downloadTab.Controls.Add(dlHint);

            dlRefreshMc.Click += delegate { RefreshMcCombo(); };
            dlRefreshLoader.Click += delegate { RefreshLoaderCombo(); };
            _dlMcBox.SelectedIndexChanged += delegate { RefreshLoaderCombo(); };
            _dlLoaderBox.SelectedIndexChanged += delegate { RefreshLoaderCombo(); };

            var vanillaTab = new TabPage("原版文件");
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
            _projectType.Items.AddRange(new object[] { "Mod", "整合包", "数据包", "资源包", "光影包" });
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
            _downloadProject.SetBounds(16, 314, 150, 36);
            modrinthTab.Controls.Add(_downloadProject);

            _installModpack = new Button();
            _installModpack.Text = "安装 .mrpack 整合包…";
            _installModpack.SetBounds(176, 314, 190, 36);
            modrinthTab.Controls.Add(_installModpack);

            var modrinthNote = new Label();
            modrinthNote.Text = "整合包下载只保存文件；点「安装 .mrpack」可把整合包展开成 PCL 能识别的实例（支持原版 / Fabric / Quilt，Forge 需官方安装器）。\r\nPCL 2.99 官方版禁用了自带导入，本工具不修改 PCL。Mod / 资源文件按 SHA-512 校验。";
            modrinthNote.SetBounds(16, 356, 790, 52);
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

            var loaderTab = new TabPage("加载器");
            tabs.TabPages.Add(loaderTab);

            var loaderTypeLabel = new Label();
            loaderTypeLabel.Text = "类型";
            loaderTypeLabel.SetBounds(16, 18, 40, 22);
            loaderTab.Controls.Add(loaderTypeLabel);

            _loaderType = new ComboBox();
            _loaderType.DropDownStyle = ComboBoxStyle.DropDownList;
            _loaderType.Items.AddRange(new object[] { "Forge", "NeoForge", "Fabric", "OptiFine" });
            _loaderType.SelectedIndex = 0;
            _loaderType.SetBounds(58, 14, 130, 26);
            loaderTab.Controls.Add(_loaderType);

            var loaderMcLabel = new Label();
            loaderMcLabel.Text = "Minecraft 版本";
            loaderMcLabel.SetBounds(206, 18, 110, 22);
            loaderTab.Controls.Add(loaderMcLabel);

            _loaderMc = new TextBox();
            _loaderMc.SetBounds(318, 14, 140, 26);
            loaderTab.Controls.Add(_loaderMc);

            _loadLoaderBtn = new Button();
            _loadLoaderBtn.Text = "读取可用版本";
            _loadLoaderBtn.SetBounds(470, 13, 140, 30);
            loaderTab.Controls.Add(_loadLoaderBtn);

            _loaderList = new ListBox();
            _loaderList.SetBounds(16, 58, 790, 200);
            _loaderList.HorizontalScrollbar = true;
            loaderTab.Controls.Add(_loaderList);

            _installLoaderBtn = new Button();
            _installLoaderBtn.Text = "下载并安装所选版本";
            _installLoaderBtn.SetBounds(16, 268, 200, 38);
            loaderTab.Controls.Add(_installLoaderBtn);

            _quickInstallBtn = new Button();
            _quickInstallBtn.Text = "一键装最新版（自动选最新 MC + 最新加载器）";
            _quickInstallBtn.SetBounds(16, 316, 380, 40);
            _quickInstallBtn.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            loaderTab.Controls.Add(_quickInstallBtn);

            var loaderNote = new Label();
            loaderNote.Text = "Forge / NeoForge：下载官方安装器并调用 Java 静默安装（需本机 Java 17+）。\r\nFabric：直接生成实例，不需要安装器。OptiFine：只下载安装 jar，请手动双击安装（官方安装器需要交互）。";
            loaderNote.SetBounds(230, 264, 580, 60);
            loaderTab.Controls.Add(loaderNote);

            var outputLabel = new Label();
            outputLabel.Text = "Modrinth / 直接下载保存到";
            outputLabel.SetBounds(20, 528, 260, 22);
            Controls.Add(outputLabel);

            _outputDir = new TextBox();
            _outputDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PCL-Download-Restore");
            _outputDir.SetBounds(20, 550, 700, 26);
            Controls.Add(_outputDir);

            var browseOutput = new Button();
            browseOutput.Text = "选择…";
            browseOutput.SetBounds(730, 549, 120, 28);
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

            var proxyLabel = new Label();
            proxyLabel.Text = "代理（可空）";
            proxyLabel.SetBounds(20, 586, 200, 22);
            Controls.Add(proxyLabel);

            _proxyBox = new TextBox();
            _proxyBox.SetBounds(20, 608, 380, 26);
            Controls.Add(_proxyBox);
            _proxyBox.TextChanged += delegate { Engine.SetProxy(_proxyBox.Text); };

            var cfLabel = new Label();
            cfLabel.Text = "CurseForge API Key（可空，填了才能搜 CF）";
            cfLabel.SetBounds(420, 586, 400, 22);
            Controls.Add(cfLabel);

            _cfKeyBox = new TextBox();
            _cfKeyBox.SetBounds(420, 608, 420, 26);
            _cfKeyBox.UseSystemPasswordChar = true;
            Controls.Add(_cfKeyBox);
            _cfKeyBox.TextChanged += delegate { Engine.SetCfKey(_cfKeyBox.Text); };

            _status = new Label();
            _status.Text = "就绪。";
            _status.SetBounds(20, 646, 700, 22);
            Controls.Add(_status);

            _progress = new ProgressBar();
            _progress.SetBounds(20, 670, 700, 16);
            Controls.Add(_progress);

            _cancelButton = new Button();
            _cancelButton.Text = "取消";
            _cancelButton.Enabled = false;
            _cancelButton.SetBounds(730, 662, 120, 30);
            Controls.Add(_cancelButton);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font("Consolas", 8.5F);
            _log.SetBounds(20, 694, 830, 80);
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
            _installModpack.Click += delegate { StartJob(JobKind.InstallModpack); };
            _directButton.Click += delegate { StartJob(JobKind.Direct); };
            _loadLoaderBtn.Click += delegate { StartJob(JobKind.LoadLoaderVersions); };
            _installLoaderBtn.Click += delegate { StartJob(JobKind.InstallLoader); };
            _quickInstallBtn.Click += delegate { StartJob(JobKind.QuickInstall); };
            _dlStartBtn.Click += delegate { StartJob(JobKind.DownloadGame); };
            _cancelButton.Click += delegate
            {
                if (_cts != null) _cts.Cancel();
                _status.Text = "正在取消…";
            };
            _searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !_worker.IsBusy) { StartJob(JobKind.Search); e.SuppressKeyPress = true; }
            };

            try
            {
                File.WriteAllText(Path.Combine(Application.StartupPath, "PCLDownloadRestore.log"), "", new UTF8Encoding(false));
            }
            catch { }

            AppendLog("PCL 下载恢复工具已启动（v1.7.0）。");
            AppendLog("本程序不启动游戏、不执行下载内容，也不需要 Microsoft/Mojang 登录令牌。");
            AppendLog("原版下载会写入所选游戏目录，建议先备份现有实例。");

            LoadSettings();
            FormClosing += delegate { SaveSettings(); };
            ApplyTheme();
            RefreshMcCombo();
        }

        private static string DetectGameRoot()
        {
            string baseDir = Application.StartupPath;
            // PCL 不同版本可能把设置放在 exe 同级或 PCL 子目录，两个都看，外层优先。
            string[] inis = new string[]
            {
                Path.Combine(baseDir, "Setup.ini"),
                Path.Combine(baseDir, "PCL", "Setup.ini")
            };
            foreach (string ini in inis)
            {
                try
                {
                    if (!File.Exists(ini)) continue;
                    foreach (string line in File.ReadAllLines(ini, Encoding.UTF8))
                    {
                        if (!line.StartsWith("LaunchFolderSelect:", StringComparison.OrdinalIgnoreCase)) continue;
                        string value = line.Substring("LaunchFolderSelect:".Length).Trim();
                        if (value.Length == 0) continue;
                        if (value.StartsWith("$", StringComparison.Ordinal))
                        {
                            string rest = value.Substring(1).TrimStart('\\', '/');
                            value = Path.Combine(baseDir, rest);
                        }
                        if (Directory.Exists(value)) return Path.GetFullPath(value).TrimEnd('\\', '/');
                    }
                }
                catch { }
            }
            foreach (string candidate in new string[]
            {
                Path.Combine(baseDir, ".minecraft"),
                Path.Combine(baseDir, "minecraft")
            })
            {
                if (Directory.Exists(candidate)) return candidate.TrimEnd('\\', '/');
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        }

        private static string SettingsFile
        {
            get { return Path.Combine(Application.StartupPath, "PCLDownloadRestore.ini"); }
        }

        private void ApplyOpacity(int percent)
        {
            if (percent < 30) percent = 30;
            if (percent > 100) percent = 100;
            Opacity = percent / 100.0;
            if (_opacityLabel != null) _opacityLabel.Text = percent + "%";
        }

        private void ApplyBackground(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    BackgroundImage = null;
                    BackColor = Color.FromArgb(245, 248, 252);
                    return;
                }
                using (var image = Image.FromFile(path))
                {
                    BackgroundImage = new Bitmap(image);
                }
                BackgroundImageLayout = ImageLayout.Zoom;
                BackColor = Color.FromArgb(28, 34, 44);
            }
            catch { }
        }

        private void ChangeBackground()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择背景图片";
                dialog.Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _bgImagePath = dialog.FileName;
                ApplyBackground(_bgImagePath);
            }
        }

        private void ApplyTheme()
        {
            var accent = Color.FromArgb(42, 111, 190);
            ApplyButtonTheme(this, accent);
            foreach (TabControl tabs in FindTabs(this))
            {
                foreach (TabPage page in tabs.TabPages) page.BackColor = Color.White;
            }
        }

        private static IEnumerable<TabControl> FindTabs(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                var tabs = child as TabControl;
                if (tabs != null)
                {
                    yield return tabs;
                    continue;
                }
                foreach (TabControl nested in FindTabs(child)) yield return nested;
            }
        }

        private static void ApplyButtonTheme(Control parent, Color accent)
        {
            foreach (Control child in parent.Controls)
            {
                var button = child as Button;
                if (button != null)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderSize = 0;
                    button.BackColor = accent;
                    button.ForeColor = Color.White;
                    button.Cursor = Cursors.Hand;
                }
                if (child.HasChildren) ApplyButtonTheme(child, accent);
            }
        }

        private void DrawTabItem(object sender, DrawItemEventArgs e)
        {
            var control = (TabControl)sender;
            if (e.Index < 0 || e.Index >= control.TabPages.Count) return;
            TabPage page = control.TabPages[e.Index];
            bool selected = e.Index == control.SelectedIndex;
            Rectangle bounds = e.Bounds;

            using (var back = new SolidBrush(selected ? Color.White : Color.FromArgb(228, 236, 248)))
            {
                e.Graphics.FillRectangle(back, bounds);
            }
            if (selected)
            {
                using (var accent = new SolidBrush(Color.FromArgb(42, 111, 190)))
                {
                    e.Graphics.FillRectangle(accent, bounds.Left + 10, bounds.Bottom - 3, bounds.Width - 20, 3);
                }
            }
            TextRenderer.DrawText(e.Graphics, page.Text, control.Font, bounds,
                selected ? Color.FromArgb(28, 55, 95) : Color.FromArgb(95, 110, 130),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                {
                    int index = line.IndexOf('=');
                    if (index <= 0) continue;
                    string key = line.Substring(0, index).Trim();
                    string value = line.Substring(index + 1).Trim();
                    if (value.Length == 0) continue;
                    if (key == "gameRoot" && Directory.Exists(value)) _gameRoot.Text = value.TrimEnd('\\', '/');
                    else if (key == "outputDir") _outputDir.Text = value.TrimEnd('\\', '/');
                    else if (key == "proxy") { _proxyBox.Text = value; Engine.SetProxy(value); }
                    else if (key == "cfKey") { _cfKeyBox.Text = value; Engine.SetCfKey(value); }
                    else if (key == "bgImagePath") { _bgImagePath = value; ApplyBackground(value); }
                    else if (key == "opacity")
                    {
                        int percent;
                        if (int.TryParse(value, out percent))
                        {
                            _opacitySlider.Value = Math.Max(30, Math.Min(100, percent));
                            ApplyOpacity(_opacitySlider.Value);
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsFile, new string[]
                {
                    "gameRoot=" + _gameRoot.Text.TrimEnd('\\', '/'),
                    "outputDir=" + _outputDir.Text.TrimEnd('\\', '/'),
                    "proxy=" + _proxyBox.Text,
                    "cfKey=" + _cfKeyBox.Text,
                    "bgImagePath=" + _bgImagePath,
                    "opacity=" + _opacitySlider.Value
                }, new UTF8Encoding(false));
            }
            catch { }
        }

        private enum JobKind
        {
            LoadVersions,
            Vanilla,
            Search,
            Project,
            Direct,
            InstallModpack,
            LoadLoaderVersions,
            InstallLoader,
            QuickInstall,
            DownloadGame
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
                if (ex.Message != "已取消。") MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                case JobKind.InstallModpack:
                {
                    using (var dialog = new OpenFileDialog())
                    {
                        dialog.Title = "选择 Modrinth 整合包（.mrpack）";
                        dialog.Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*";
                        if (dialog.ShowDialog(this) != DialogResult.OK) throw new Exception("已取消。");
                        string file = dialog.FileName;
                        RequireDirectory(_gameRoot.Text);

                        var index = ReadMrpackIndex(file);
                        var deps = index.ContainsKey("dependencies") ? Engine.Obj(index["dependencies"]) : null;
                        string mcVersion = deps != null && deps.ContainsKey("minecraft") ? Engine.Str(deps["minecraft"]) : "未知";
                        string loaderText = "原版";
                        if (deps != null)
                        {
                            if (deps.ContainsKey("fabric-loader")) loaderText = "Fabric " + Engine.Str(deps["fabric-loader"]);
                            else if (deps.ContainsKey("quilt-loader")) loaderText = "Quilt " + Engine.Str(deps["quilt-loader"]);
                            else if (deps.ContainsKey("forge")) loaderText = "Forge " + Engine.Str(deps["forge"]) + "（安装时调用官方安装器，需 Java）";
                            else if (deps.ContainsKey("neoforge")) loaderText = "NeoForge " + Engine.Str(deps["neoforge"]) + "（安装时调用官方安装器，需 Java）";
                        }
                        var files = Engine.Arr(index.ContainsKey("files") ? index["files"] : null);
                        long totalSize = 0;
                        int fileCount = 0;
                        if (files != null)
                        {
                            foreach (object rawFile in files)
                            {
                                var item = Engine.Obj(rawFile);
                                if (item == null) continue;
                                fileCount++;
                                totalSize += Engine.Long(item.ContainsKey("fileSize") ? item["fileSize"] : null);
                            }
                        }
                        string packName = Engine.Str(index.ContainsKey("name") ? index["name"] : null);
                        string message = "整合包：" + (string.IsNullOrEmpty(packName) ? "(未命名)" : packName) +
                                         "\r\nMinecraft：" + mcVersion +
                                         "\r\n加载器：" + loaderText +
                                         "\r\n文件数：" + fileCount +
                                         "\r\n总大小：" + (totalSize / 1024 / 1024) + " MB" +
                                         "\r\n\r\n将安装到：" + Path.Combine(_gameRoot.Text, "versions") +
                                         "\r\n\r\n继续安装吗？";
                        if (MessageBox.Show(this, message, "确认安装整合包", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                            throw new Exception("已取消。");
                        return file;
                    }
                }
                case JobKind.LoadLoaderVersions:
                    return null;
                case JobKind.DownloadGame:
                {
                    string mc = Convert.ToString(_dlMcBox.SelectedItem);
                    if (string.IsNullOrEmpty(mc)) throw new Exception("请先选择 Minecraft 版本（点「刷新」加载列表）。");
                    string loaderType = Convert.ToString(_dlLoaderBox.SelectedItem);
                    if (string.IsNullOrEmpty(loaderType) || loaderType.StartsWith("无", StringComparison.Ordinal))
                        return new GameRequest { McVersion = mc, LoaderType = "无" };

                    LoaderEntry loader = _dlLoaderVersionBox.SelectedItem as LoaderEntry;
                    if (loader == null)
                    {
                        foreach (object item in _dlLoaderVersionBox.Items)
                        {
                            loader = item as LoaderEntry;
                            if (loader != null) break;
                        }
                    }
                    if (loader == null) throw new Exception("还没读取到加载器版本，请等几秒或点「刷新版本」。");
                    return new GameRequest
                    {
                        McVersion = mc,
                        LoaderType = loaderType,
                        LoaderVersion = loader.Version,
                        OptiFineType = loader.ExtraType,
                        OptiFinePatch = loader.ExtraPatch
                    };
                }
                case JobKind.InstallLoader:
                    if (_loaderList.SelectedIndex < 0 || _loaderList.SelectedIndex >= _loaderEntries.Count)
                        throw new Exception("请先读取并选择加载器版本。");
                    RequireDirectory(_gameRoot.Text);
                    return _loaderEntries[_loaderList.SelectedIndex];
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
            _installModpack.Enabled = !busy;
            _loadLoaderBtn.Enabled = !busy;
            _installLoaderBtn.Enabled = !busy;
            _quickInstallBtn.Enabled = !busy;
            _dlStartBtn.Enabled = !busy;
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
                    case JobKind.InstallModpack: DoInstallModpack((string)_jobArgument, token); break;
                    case JobKind.LoadLoaderVersions: DoLoadLoaderVersions(token); break;
                    case JobKind.InstallLoader: DoInstallLoader((LoaderEntry)_jobArgument, token); break;
                    case JobKind.QuickInstall: DoQuickInstall(token); break;
                    case JobKind.DownloadGame: DoDownloadGame((GameRequest)_jobArgument, token); break;
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
            string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            _log.AppendText("[" + stamp + "] " + text + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            try
            {
                File.AppendAllText(Path.Combine(Application.StartupPath, "PCLDownloadRestore.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + text + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
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
            InstallVanillaFiles(entry, _gameRoot.Text, _includeAssets.Checked, token);
            LogLine("完成：" + entry.Id + "；根目录：" + _gameRoot.Text);
            MessageBox.Show(this,
                "版本 " + entry.Id + " 的官方文件已下载到：" + Environment.NewLine + _gameRoot.Text + Environment.NewLine + Environment.NewLine +
                "本工具不会启动游戏。请在 PCL 中刷新版本列表；首次启动仍需正版账号验证。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Report("版本 " + entry.Id + " 已下载完成。");
            OpenFolder(Path.Combine(_gameRoot.Text, "versions", entry.Id));
        }

        private void InstallVanillaFiles(VersionEntry entry, string root, bool includeAssets, CancellationToken token)
        {
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

            if (includeAssets)
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
            if (total > 0)
            {
                var options = new ParallelOptions();
                options.MaxDegreeOfParallelism = 4;
                options.CancellationToken = token;
                try
                {
                    Parallel.ForEach(tasks, options, delegate(DownloadTask task)
                    {
                        token.ThrowIfCancellationRequested();
                        int current = Interlocked.Increment(ref done);
                        Report(string.Format("下载文件：{0} / {1} — {2}", current, total, task.Label));
                        ReportProgressCount(current, total);
                        Engine.Download(task.Url, task.Path, task.Hash, task.Algo, task.Size, task.Kind, this, token);
                        if (current % 25 == 0) LogLine(string.Format("已完成 {0} / {1} 个文件。", current, total));
                    });
                }
                catch (AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        if (inner is OperationCanceledException) throw new OperationCanceledException(token);
                        throw new Exception(inner.Message);
                    }
                }
            }
            ReportProgressCount(total, Math.Max(1, total));
        }

        private string ProjectTypeValue()
        {
            switch (Convert.ToString(_projectType.SelectedItem))
            {
                case "Mod": return "mod";
                case "数据包": return "datapack";
                case "资源包": return "resourcepack";
                case "光影包": return "shader";
                default: return "modpack";
            }
        }

        private static readonly Dictionary<string, string> ModAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "钠", "sodium" },
            { "锂", "lithium" },
            { "铱", "iris" },
            { "铱光影", "iris shaders" },
            { "铁氧体磁芯", "ferritecore" },
            { "模组菜单", "mod menu" },
            { "实体渲染机制优化", "entity culling" },
            { "布料配置", "cloth config" },
            { "立即快速", "immediatelyfast" },
            { "苹果皮", "appleskin" },
            { "合成表", "jei" },
            { "小地图", "xaero" },
            { "光影包", "shader" },
            { "性能优化", "performance" },
            { "区块预加载", "chunky" },
            { "光影", "shaders" }
        };

        private List<ProjectEntry> RunModrinthSearch(string query, string type, CancellationToken token)
        {
            string url = "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(query);
            if (!string.IsNullOrEmpty(type))
                url += "&facets=" + Uri.EscapeDataString("[[\"project_type:" + type + "\"]]");
            url += "&limit=30&index=relevance";
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
                        Description = Engine.Str(item.ContainsKey("description") ? item["description"] : null),
                        TypeLabel = TypeName(Engine.Str(item.ContainsKey("project_type") ? item["project_type"] : null))
                    });
                }
            }
            return projects;
        }

        private static string TypeName(string type)
        {
            switch (type)
            {
                case "mod": return "Mod";
                case "modpack": return "整合包";
                case "datapack": return "数据包";
                case "resourcepack": return "资源包";
                case "shader": return "光影";
                default: return type;
            }
        }

        private void DoSearch(CancellationToken token)
        {
            string query = _searchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) throw new Exception("请输入搜索关键词。");
            string type = ProjectTypeValue();
            Report("正在 Modrinth 搜索 " + type + "…");
            var projects = RunModrinthSearch(query, type, token);
            if (projects.Count == 0)
            {
                string alias;
                if (ModAliases.TryGetValue(query, out alias))
                {
                    LogLine("中文关键词没结果，改用英文名重试：" + alias);
                    projects = RunModrinthSearch(alias, type, token);
                }
            }
            if (projects.Count == 0)
            {
                LogLine("当前类型没结果，改为全类型再搜一次。");
                projects = RunModrinthSearch(query, null, token);
            }
            if (!string.IsNullOrEmpty(Engine.CfKey))
            {
                try
                {
                    var cf = SearchCurseForge(query, ClassIdForType(type), token);
                    if (cf.Count > 0)
                    {
                        cf.AddRange(projects);
                        projects = cf;
                        LogLine("CurseForge 返回 " + cf.Count + " 条（含 Modrinth）。");
                    }
                }
                catch (Exception ex)
                {
                    LogLine("CurseForge 搜索失败：" + ex.Message);
                }
            }
            _projects = projects;
            BeginInvoke(new Action(delegate
            {
                _results.Items.Clear();
                foreach (ProjectEntry project in _projects) _results.Items.Add(project);
            }));
            Report(string.Format("找到 {0} 个项目。", projects.Count));
            if (projects.Count == 0)
                LogLine("没有结果：Modrinth 以英文为主，试试英文关键词，或把类型切到 Mod。");
            else
                LogLine("Modrinth 搜索完成。");
        }

        private static int ClassIdForType(string type)
        {
            switch (type)
            {
                case "mod": return 6;
                case "modpack": return 4471;
                case "datapack": return 6945;
                case "resourcepack": return 12;
                case "shader": return 6552;
                default: return 6;
            }
        }

        private static string ClassTypeName(int classId)
        {
            switch (classId)
            {
                case 6: return "mod";
                case 4471: return "modpack";
                case 6945: return "datapack";
                case 12: return "resourcepack";
                case 6552: return "shader";
                default: return "";
            }
        }

        private List<ProjectEntry> SearchCurseForge(string query, int classId, CancellationToken token)
        {
            string url = "https://api.curseforge.com/v1/mods/search?gameId=432&classId=" + classId +
                         "&searchFilter=" + Uri.EscapeDataString(query) + "&pageSize=20&sortField=2&sortOrder=desc";
            string json = Engine.GetCurseForgeJson(url);
            var root = Engine.ParseObject(json);
            var data = Engine.Arr(root.ContainsKey("data") ? root["data"] : null);
            var projects = new List<ProjectEntry>();
            if (data == null) return projects;
            foreach (object raw in data)
            {
                token.ThrowIfCancellationRequested();
                var item = Engine.Obj(raw);
                if (item == null) continue;
                projects.Add(new ProjectEntry
                {
                    Id = Engine.Str(item.ContainsKey("id") ? item["id"] : null),
                    Title = Engine.Str(item.ContainsKey("name") ? item["name"] : null),
                    Description = Engine.Str(item.ContainsKey("summary") ? item["summary"] : null),
                    TypeLabel = TypeName(ClassTypeName(classId)),
                    Source = "curseforge",
                    Slug = Engine.Str(item.ContainsKey("slug") ? item["slug"] : null)
                });
            }
            return projects;
        }

        private void DoProjectCurseForge(ProjectEntry project, CancellationToken token)
        {
            Report("读取 CurseForge 文件列表…");
            string url = "https://api.curseforge.com/v1/mods/" + project.Id + "/files?pageSize=50";
            string json = Engine.GetCurseForgeJson(url);
            var root = Engine.ParseObject(json);
            var data = Engine.Arr(root.ContainsKey("data") ? root["data"] : null);
            if (data == null || data.Length == 0) throw new Exception("该项目没有可下载文件。");
            string gameFilter = _filterGameVersion.Text.Trim();
            Dictionary<string, object> chosen = null;
            foreach (object raw in data)
            {
                var file = Engine.Obj(raw);
                if (file == null) continue;
                if (!string.IsNullOrEmpty(gameFilter))
                {
                    var versions = Engine.Arr(file.ContainsKey("gameVersions") ? file["gameVersions"] : null);
                    bool match = false;
                    if (versions != null)
                    {
                        foreach (object version in versions)
                        {
                            if (string.Equals(Engine.Str(version), gameFilter, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                        }
                    }
                    if (!match) continue;
                }
                chosen = file;
                break;
            }
            if (chosen == null) throw new Exception("没有匹配游戏版本的文件，清空筛选后重试。");
            string downloadUrl = Engine.Str(chosen.ContainsKey("downloadUrl") ? chosen["downloadUrl"] : null);
            if (string.IsNullOrEmpty(downloadUrl))
                throw new Exception("该文件作者禁止第三方下载（downloadUrl 为空），请到 CurseForge 网页手动下载。");
            string fileName = Engine.Str(chosen.ContainsKey("fileName") ? chosen["fileName"] : null);
            if (string.IsNullOrEmpty(fileName)) fileName = project.Title + ".jar";
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Exception("文件名无效。");

            string folder = _outputDir.Text;
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, fileName);
            if (File.Exists(destination))
            {
                destination = Path.Combine(folder,
                    Path.GetFileNameWithoutExtension(fileName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(fileName));
            }
            Report("下载 CurseForge 文件：" + fileName);
            Engine.Download(downloadUrl, destination, null, "SHA1", 0, "curseforge", this, token);
            LogLine("已保存：" + destination);
            MessageBox.Show(this, "文件已保存：" + Environment.NewLine + destination, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenFolder(folder);
            Report("下载完成。");
        }

        private void DoProject(ProjectEntry project, CancellationToken token)
        {
            if (project.Source == "curseforge")
            {
                DoProjectCurseForge(project, token);
                return;
            }
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
            OpenFolder(folder);
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
            OpenFolder(folder);
        }

        // ===== 整合包安装（.mrpack）=====

        private static Dictionary<string, object> ReadMrpackIndex(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.FullName == "modrinth.index.json")
                    {
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        {
                            return Engine.ParseObject(reader.ReadToEnd());
                        }
                    }
                }
            }
            throw new Exception("不是 Modrinth 整合包：缺少 modrinth.index.json。");
        }

        private void InstallForgeLike(string gameRoot, string mcVersion, string loaderVersion, bool neoForge, CancellationToken token)
        {
            string java = FindJava();
            if (string.IsNullOrEmpty(java))
                throw new Exception("找不到 Java。安装 Forge / NeoForge 需要 Java 运行时：\r\n" +
                                    "请安装 JDK 17+，或把 java.exe 加入 PATH / 设置 JAVA_HOME 后重试。");

            string installerUrl;
            if (neoForge)
                installerUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/" + loaderVersion + "/neoforge-" + loaderVersion + "-installer.jar";
            else
                installerUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/" + mcVersion + "-" + loaderVersion + "/forge-" + mcVersion + "-" + loaderVersion + "-installer.jar";

            string tempDir = Path.Combine(Path.GetTempPath(), "pcl-forge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string installer = Path.Combine(tempDir, "installer.jar");
                Report("下载 " + (neoForge ? "NeoForge" : "Forge") + " 安装器…");
                Engine.Download(installerUrl, installer, null, "SHA1", 0, "direct", this, token);
                LogLine("安装目标目录：" + gameRoot.TrimEnd('\\', '/'));
                Report("运行安装器（需要联网下载库，可能要几分钟）…");
                RunJavaInstaller(java, installer, gameRoot, token);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private void RunJavaInstaller(string java, string installer, string gameRoot, CancellationToken token)
        {
            // 关键：路径末尾不能有反斜杠，否则 "C:\path\" 里的 \" 会被 Java 当成转义引号，
            // 安装器会拿到带引号的路径并报 “There is no Minecraft installation”。
            string rootArg = gameRoot.TrimEnd('\\', '/');
            if (!Directory.Exists(rootArg)) Directory.CreateDirectory(rootArg);
            string versionsDir = Path.Combine(rootArg, "versions");
            if (!Directory.Exists(versionsDir)) Directory.CreateDirectory(versionsDir);

            var startInfo = new System.Diagnostics.ProcessStartInfo();
            startInfo.FileName = java;
            startInfo.Arguments = "-jar \"" + installer.TrimEnd('\\', '/') + "\" --installClient \"" + rootArg + "\"";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += delegate(object s, System.Diagnostics.DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) LogLine("[安装器] " + e.Data);
                };
                process.ErrorDataReceived += delegate(object s, System.Diagnostics.DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) LogLine("[安装器] " + e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (token.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch { } }))
                {
                    while (!process.WaitForExit(500))
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new Exception("安装器退出码 " + process.ExitCode + "。常见原因：Java 版本不匹配、网络被拦截，或该版本安装器不支持 --installClient。");
            }
        }

        private static string FindJava()
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string exe = Path.Combine(dir.Trim(), "java.exe");
                        if (File.Exists(exe)) return exe;
                    }
                    catch { }
                }
            }
            string home = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(home))
            {
                try
                {
                    string exe = Path.Combine(home, "bin", "java.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch { }
            }
            string[] roots = new string[]
            {
                @"C:\Program Files\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Microsoft\jdk",
                @"C:\Program Files\Zulu",
                @"C:\Program Files\BellSoft",
                @"C:\Program Files\Amazon Corretto",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks")
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string exe = Path.Combine(dir, "bin", "java.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FindLoaderVersionDir(string gameRoot, string mcVersion, string loaderVersion, bool neo)
        {
            string versionsRoot = Path.Combine(gameRoot, "versions");
            if (!Directory.Exists(versionsRoot)) return null;
            foreach (string dir in Directory.GetDirectories(versionsRoot))
            {
                string name = Path.GetFileName(dir);
                if (neo)
                {
                    if (name.IndexOf("neoforge", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf(loaderVersion, StringComparison.OrdinalIgnoreCase) >= 0) return name;
                }
                else
                {
                    if (name.IndexOf("forge", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf(mcVersion, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf(loaderVersion, StringComparison.OrdinalIgnoreCase) >= 0) return name;
                }
            }
            return null;
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        private void DoInstallModpack(string archivePath, CancellationToken token)
        {
            if (!File.Exists(archivePath)) throw new Exception("找不到整合包文件：" + archivePath);
            string gameRoot = _gameRoot.Text;
            string work = Path.Combine(Path.GetTempPath(), "pcl-mrpack-" + Guid.NewGuid().ToString("N"));
            try
            {
                Report("解压整合包…");
                ZipFile.ExtractToDirectory(archivePath, work);

                string indexFile = Path.Combine(work, "modrinth.index.json");
                if (!File.Exists(indexFile))
                    throw new Exception("这不是 Modrinth 整合包：缺少 modrinth.index.json。");

                var index = Engine.ParseObject(File.ReadAllText(indexFile, Encoding.UTF8));
                var deps = index.ContainsKey("dependencies") ? Engine.Obj(index["dependencies"]) : null;
                if (deps == null) throw new Exception("整合包缺少 dependencies 字段。");

                string mcVersion = Engine.Str(deps.ContainsKey("minecraft") ? deps["minecraft"] : null);
                if (string.IsNullOrEmpty(mcVersion)) throw new Exception("整合包没有指定 Minecraft 版本。");

                string fabricLoader = deps.ContainsKey("fabric-loader") ? Engine.Str(deps["fabric-loader"]) : null;
                string quiltLoader = deps.ContainsKey("quilt-loader") ? Engine.Str(deps["quilt-loader"]) : null;
                string forgeVersion = deps.ContainsKey("forge") ? Engine.Str(deps["forge"]) : null;
                string neoForgeVersion = deps.ContainsKey("neoforge") ? Engine.Str(deps["neoforge"]) : null;

                string displayName = Engine.Str(index.ContainsKey("name") ? index["name"] : null);
                if (string.IsNullOrEmpty(displayName)) displayName = "Modpack";
                string instance = MakeInstanceName(gameRoot, displayName);
                string instanceDir = Path.Combine(Path.Combine(gameRoot, "versions"), instance);
                Directory.CreateDirectory(instanceDir);

                Report("准备原版 " + mcVersion + " 文件…");
                var manifest = Engine.FetchManifest(this);
                VersionEntry vanilla = null;
                foreach (VersionEntry candidate in manifest)
                {
                    if (candidate.Id == mcVersion) { vanilla = candidate; break; }
                }
                if (vanilla == null) throw new Exception("Mojang 版本清单里找不到 Minecraft " + mcVersion + "。");
                InstallVanillaFiles(vanilla, gameRoot, true, token);

                string profileUrl = null;
                if (!string.IsNullOrEmpty(fabricLoader))
                {
                    profileUrl = "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(mcVersion) + "/" +
                                 Uri.EscapeDataString(fabricLoader) + "/profile/json";
                }
                else if (!string.IsNullOrEmpty(quiltLoader))
                {
                    profileUrl = "https://meta.quiltmc.org/v3/versions/loader/" + Uri.EscapeDataString(mcVersion) + "/" +
                                 Uri.EscapeDataString(quiltLoader) + "/profile/json";
                }

                string versionJson = Path.Combine(instanceDir, instance + ".json");
                if (profileUrl != null)
                {
                    Report("读取加载器配置…");
                    var profile = Engine.ParseObject(Engine.GetString(profileUrl, "direct", this));
                    var libraries = Engine.Arr(profile.ContainsKey("libraries") ? profile["libraries"] : null);
                    if (libraries != null)
                    {
                        foreach (object rawLib in libraries)
                        {
                            token.ThrowIfCancellationRequested();
                            var lib = Engine.Obj(rawLib);
                            if (lib == null) continue;
                            string coord = Engine.Str(lib.ContainsKey("name") ? lib["name"] : null);
                            if (string.IsNullOrEmpty(coord)) continue;
                            string baseUrl = lib.ContainsKey("url") ? Engine.Str(lib["url"]) : "https://maven.fabricmc.net/";
                            if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://maven.fabricmc.net/";
                            string relative = MavenRelative(coord);
                            string hash = Engine.Str(lib.ContainsKey("sha1") ? lib["sha1"] : null);
                            long size = Engine.Long(lib.ContainsKey("size") ? lib["size"] : null);
                            Report("下载加载器库：" + coord);
                            Engine.Download(baseUrl.TrimEnd('/') + "/" + relative,
                                Engine.SafeChild(gameRoot, "libraries/" + relative), hash, "SHA1", size, "library", this, token);
                        }
                    }
                    profile["id"] = instance;
                    profile["inheritsFrom"] = mcVersion;
                    WriteJsonFile(profile, versionJson);
                }
                else if (!string.IsNullOrEmpty(forgeVersion) || !string.IsNullOrEmpty(neoForgeVersion))
                {
                    bool isNeo = !string.IsNullOrEmpty(neoForgeVersion);
                    string loaderVersion = isNeo ? neoForgeVersion : forgeVersion;
                    LogLine("检测到 " + (isNeo ? "NeoForge " : "Forge ") + loaderVersion + "，将调用官方安装器。");
                    InstallForgeLike(gameRoot, mcVersion, loaderVersion, isNeo, token);
                    string loaderDir = FindLoaderVersionDir(gameRoot, mcVersion, loaderVersion, isNeo);
                    if (string.IsNullOrEmpty(loaderDir))
                        throw new Exception("安装器已结束，但没有在 versions 下找到生成的版本目录。请打开 PCL 检查是否安装成功。");
                    var stub = new Dictionary<string, object>();
                    stub["id"] = instance;
                    stub["inheritsFrom"] = loaderDir;
                    stub["type"] = "release";
                    string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                    stub["time"] = now;
                    stub["releaseTime"] = now;
                    WriteJsonFile(stub, versionJson);
                }
                else
                {
                    var stub = new Dictionary<string, object>();
                    stub["id"] = instance;
                    stub["inheritsFrom"] = mcVersion;
                    stub["type"] = "release";
                    string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                    stub["time"] = now;
                    stub["releaseTime"] = now;
                    WriteJsonFile(stub, versionJson);
                }

                var files = Engine.Arr(index.ContainsKey("files") ? index["files"] : null);
                if (files != null)
                {
                    int indexCounter = 0;
                    foreach (object rawFile in files)
                    {
                        token.ThrowIfCancellationRequested();
                        indexCounter++;
                        var file = Engine.Obj(rawFile);
                        if (file == null) continue;
                        string relativePath = Engine.Str(file.ContainsKey("path") ? file["path"] : null);
                        var downloads = Engine.Arr(file.ContainsKey("downloads") ? file["downloads"] : null);
                        string url = downloads != null && downloads.Length > 0 ? Engine.Str(downloads[0]) : null;
                        if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(url)) continue;
                        var hashes = file.ContainsKey("hashes") ? Engine.Obj(file["hashes"]) : null;
                        string sha512 = hashes != null && hashes.ContainsKey("sha512") ? Engine.Str(hashes["sha512"]) : null;
                        long size = Engine.Long(file.ContainsKey("fileSize") ? file["fileSize"] : null);
                        Report(string.Format("下载整合包文件：{0} / {1}", indexCounter, files.Length));
                        Engine.Download(url, Engine.SafeChild(instanceDir, relativePath), sha512, "SHA512", size, "modrinth", this, token);
                    }
                }

                string overrides = Path.Combine(work, "overrides");
                if (Directory.Exists(overrides))
                {
                    Report("复制整合包覆盖文件…");
                    CopyDirectory(overrides, instanceDir);
                }

                LogLine("整合包已安装为实例：" + instance);
                Report("整合包安装完成：" + instance);
                MessageBox.Show(this,
                    "整合包已安装！\r\n\r\nPCL 版本名：" + instance +
                    "\r\n位置：" + instanceDir + "\r\n\r\n" +
                    "回到 PCL 刷新版本列表，就能看到这个实例。\r\n" +
                    "首次启动仍需正版账号验证；若 PCL 提示缺少资源，请回到「原版 Minecraft」页补齐同一版本。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenFolder(instanceDir);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }

        // ===== 加载器版本列表与安装 =====

        private List<LoaderEntry> LoadLoaderEntries(string type, string mc, CancellationToken token)
        {
            var entries = new List<LoaderEntry>();
            if (type == "Forge")
            {
                string xml = Engine.GetString("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", "direct", null);
                foreach (string version in XmlVersions(xml))
                {
                    token.ThrowIfCancellationRequested();
                    int dash = version.IndexOf('-');
                    if (dash <= 0) continue;
                    string mcPart = version.Substring(0, dash);
                    string forgePart = version.Substring(dash + 1);
                    if (!string.IsNullOrEmpty(mc) && mcPart != mc) continue;
                    entries.Add(new LoaderEntry { Type = "forge", McVersion = mcPart, Version = forgePart });
                }
            }
            else if (type == "NeoForge")
            {
                string xml = Engine.GetString("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", "direct", null);
                foreach (string version in XmlVersions(xml))
                {
                    token.ThrowIfCancellationRequested();
                    if (version.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        version.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string mcPart = NeoForgeMcVersion(version);
                    if (!string.IsNullOrEmpty(mc) && mcPart != mc) continue;
                    entries.Add(new LoaderEntry { Type = "neoforge", McVersion = mcPart, Version = version });
                }
            }
            else if (type == "Fabric")
            {
                string json = Engine.GetString("https://meta.fabricmc.net/v2/versions/loader", "direct", null);
                var array = Engine.ParseArray(json);
                if (array != null)
                {
                    foreach (object raw in array)
                    {
                        token.ThrowIfCancellationRequested();
                        var item = Engine.Obj(raw);
                        if (item == null) continue;
                        string version = Engine.Str(item.ContainsKey("version") ? item["version"] : null);
                        if (string.IsNullOrEmpty(version)) continue;
                        entries.Add(new LoaderEntry { Type = "fabric", McVersion = mc, Version = version });
                    }
                }
            }
            else
            {
                string json = Engine.GetString("https://bmclapi2.bangbang93.com/optifine/versionList", "direct", null);
                var array = Engine.ParseArray(json);
                if (array != null)
                {
                    foreach (object raw in array)
                    {
                        token.ThrowIfCancellationRequested();
                        var item = Engine.Obj(raw);
                        if (item == null) continue;
                        string mcPart = Engine.Str(item.ContainsKey("mcversion") ? item["mcversion"] : null);
                        string typePart = Engine.Str(item.ContainsKey("type") ? item["type"] : null);
                        string patch = Engine.Str(item.ContainsKey("patch") ? item["patch"] : null);
                        if (string.IsNullOrEmpty(mcPart) || string.IsNullOrEmpty(typePart) || string.IsNullOrEmpty(patch)) continue;
                        if (patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrEmpty(mc) && mcPart != mc) continue;
                        entries.Add(new LoaderEntry
                        {
                            Type = "optifine", McVersion = mcPart,
                            Version = mcPart + "_" + typePart + "_" + patch,
                            ExtraType = typePart, ExtraPatch = patch
                        });
                    }
                }
            }
            entries.Reverse();
            return entries;
        }

        private void DoLoadLoaderVersions(CancellationToken token)
        {
            string type = Convert.ToString(_loaderType.SelectedItem);
            string mc = _loaderMc.Text.Trim();
            Report("读取 " + type + " 版本列表…");
            var entries = LoadLoaderEntries(type, mc, token);
            if (entries.Count > 800) entries = entries.GetRange(0, 800);
            _loaderEntries = entries;
            BeginInvoke(new Action(delegate
            {
                _loaderList.Items.Clear();
                foreach (LoaderEntry entry in _loaderEntries) _loaderList.Items.Add(entry);
            }));
            Report("共 " + entries.Count + " 个版本。");
            LogLine(type + " 版本列表已载入：" + entries.Count + " 个。");
        }

        private void DoInstallLoader(LoaderEntry entry, CancellationToken token)
        {
            string gameRoot = _gameRoot.Text;
            if (entry.Type == "forge" || entry.Type == "neoforge")
            {
                bool neo = entry.Type == "neoforge";
                InstallForgeLike(gameRoot, entry.McVersion, entry.Version, neo, token);
                LogLine((neo ? "NeoForge " : "Forge ") + entry.Version + " 安装器已完成。");
                MessageBox.Show(this,
                    (neo ? "NeoForge " : "Forge ") + entry.Version + " 已安装。\r\n\r\n" +
                    "回到 PCL 刷新版本列表，选择新出现的 " + entry.McVersion + (neo ? "-neoforge-" : "-forge-") + entry.Version + " 版本即可。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenFolder(Path.Combine(gameRoot, "versions"));
                Report("安装完成。");
                return;
            }

            if (entry.Type == "fabric")
            {
                if (string.IsNullOrEmpty(entry.McVersion))
                    throw new Exception("Fabric 安装需要先填写 Minecraft 版本。");
                var manifest = Engine.FetchManifest(this);
                VersionEntry vanilla = null;
                foreach (VersionEntry candidate in manifest)
                {
                    if (candidate.Id == entry.McVersion) { vanilla = candidate; break; }
                }
                if (vanilla == null) throw new Exception("Mojang 版本清单里找不到 Minecraft " + entry.McVersion + "。");
                Report("准备原版 " + entry.McVersion + " 文件…");
                InstallVanillaFiles(vanilla, gameRoot, true, token);
                string instance = MakeInstanceName(gameRoot, entry.McVersion + "-Fabric_" + entry.Version);
                Report("生成 Fabric 实例…");
                InstallFabricLoader(gameRoot, entry.McVersion, entry.Version, instance, this, token);
                LogLine("Fabric 实例已创建：" + instance);
                MessageBox.Show(this,
                    "Fabric 已安装。\r\n\r\nPCL 版本名：" + instance + "\r\n\r\n回到 PCL 刷新版本列表即可看到。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenFolder(Path.Combine(gameRoot, "versions", instance));
                Report("安装完成。");
                return;
            }

            // OptiFine：只下载官方安装器
            string url = "https://bmclapi2.bangbang93.com/optifine/" + entry.McVersion + "/" + entry.ExtraType + "/" + entry.ExtraPatch;
            string dest = Path.Combine(gameRoot, "OptiFine_" + entry.McVersion + "_" + entry.ExtraType + "_" + entry.ExtraPatch + ".jar");
            Report("下载 OptiFine " + entry.Version + "…");
            Engine.Download(url, dest, null, "SHA1", 0, "direct", this, token);
            LogLine("OptiFine 安装器已下载：" + dest);
            MessageBox.Show(this,
                "OptiFine 安装器已下载到：\r\n" + dest + "\r\n\r\n" +
                "请手动双击运行它完成安装（官方安装器需要图形界面，本工具不能静默安装）。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenFolder(gameRoot);
            Report("下载完成。");
        }

        private void RefreshMcCombo()
        {
            var thread = new Thread(delegate()
            {
                try
                {
                    var manifest = Engine.FetchManifest(null);
                    BeginInvoke(new Action(delegate
                    {
                        string current = Convert.ToString(_dlMcBox.SelectedItem);
                        _dlMcBox.Items.Clear();
                        foreach (VersionEntry entry in manifest) _dlMcBox.Items.Add(entry.Id);
                        int index = current.Length > 0 ? _dlMcBox.Items.IndexOf(current) : -1;
                        if (index >= 0) _dlMcBox.SelectedIndex = index;
                        else if (_dlMcBox.Items.Count > 0) _dlMcBox.SelectedIndex = 0;
                    }));
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void RefreshLoaderCombo()
        {
            string type = Convert.ToString(_dlLoaderBox.SelectedItem);
            string mc = Convert.ToString(_dlMcBox.SelectedItem);
            _dlLoaderVersionBox.Items.Clear();
            if (string.IsNullOrEmpty(type) || type.StartsWith("无", StringComparison.Ordinal) || string.IsNullOrEmpty(mc)) return;
            var thread = new Thread(delegate()
            {
                try
                {
                    var entries = LoadLoaderEntries(type, mc, CancellationToken.None);
                    BeginInvoke(new Action(delegate
                    {
                        string currentType = Convert.ToString(_dlLoaderBox.SelectedItem);
                        if (currentType != type) return;
                        _dlLoaderVersionBox.Items.Clear();
                        foreach (LoaderEntry entry in entries) _dlLoaderVersionBox.Items.Add(entry);
                        if (_dlLoaderVersionBox.Items.Count > 0) _dlLoaderVersionBox.SelectedIndex = 0;
                    }));
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void DoDownloadGame(GameRequest request, CancellationToken token)
        {
            string gameRoot = _gameRoot.Text.TrimEnd('\\', '/');
            Report("准备 " + request.McVersion + " 原版文件…");
            var manifest = Engine.FetchManifest(this);
            VersionEntry vanilla = null;
            foreach (VersionEntry entry in manifest)
            {
                if (entry.Id == request.McVersion) { vanilla = entry; break; }
            }
            if (vanilla == null) throw new Exception("Mojang 版本清单里找不到 " + request.McVersion);
            InstallVanillaFiles(vanilla, gameRoot, true, token);

            if (request.LoaderType == "无")
            {
                LogLine("原版 " + request.McVersion + " 下载完成。");
                MessageBox.Show(this,
                    "原版 " + request.McVersion + " 已下载完成。\r\n\r\n回到 PCL 刷新版本列表即可看到。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenFolder(Path.Combine(gameRoot, "versions", request.McVersion));
                Report("下载完成。");
                return;
            }

            LogLine("安装加载器：" + request.LoaderType + " " + request.LoaderVersion);
            if (request.LoaderType == "Fabric")
            {
                string instance = MakeInstanceName(gameRoot, request.McVersion + "-Fabric_" + request.LoaderVersion);
                InstallFabricLoader(gameRoot, request.McVersion, request.LoaderVersion, instance, this, token);
                MessageBox.Show(this,
                    "Fabric 实例已创建：" + instance + "\r\n\r\n回到 PCL 刷新版本列表即可看到。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenFolder(Path.Combine(gameRoot, "versions", instance));
            }
            else if (request.LoaderType == "Forge" || request.LoaderType == "NeoForge")
            {
                bool neo = request.LoaderType == "NeoForge";
                InstallForgeLike(gameRoot, request.McVersion, request.LoaderVersion, neo, token);
                MessageBox.Show(this,
                    (neo ? "NeoForge " : "Forge ") + request.LoaderVersion + " 已安装。\r\n\r\n回到 PCL 刷新版本列表，选择新出现的游戏版本。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenFolder(Path.Combine(gameRoot, "versions"));
            }
            else if (request.LoaderType == "OptiFine")
            {
                string url = "https://bmclapi2.bangbang93.com/optifine/" + request.McVersion + "/" + request.OptiFineType + "/" + request.OptiFinePatch;
                string dest = Path.Combine(gameRoot, "OptiFine_" + request.McVersion + "_" + request.OptiFineType + "_" + request.OptiFinePatch + ".jar");
                Report("下载 OptiFine 安装器…");
                Engine.Download(url, dest, null, "SHA1", 0, "direct", this, token);
                MessageBox.Show(this,
                    "OptiFine 安装器已下载：\r\n" + dest + "\r\n\r\n请手动双击运行它完成安装。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenFolder(gameRoot);
            }
            Report("下载完成。");
        }

        private void DoQuickInstall(CancellationToken token)
        {
            string type = Convert.ToString(_loaderType.SelectedItem);
            Report("正在确定最新版本…");
            var manifest = Engine.FetchManifest(this);
            string mc = null;
            foreach (VersionEntry candidate in manifest)
            {
                if (candidate.Type == "release") { mc = candidate.Id; break; }
            }
            if (string.IsNullOrEmpty(mc)) throw new Exception("无法确定最新 Minecraft 版本。");
            BeginInvoke(new Action(delegate { _loaderMc.Text = mc; }));

            LoaderEntry entry = null;
            if (type == "Forge")
            {
                string xml = Engine.GetString("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", "direct", null);
                string latest = null;
                foreach (string version in XmlVersions(xml))
                {
                    token.ThrowIfCancellationRequested();
                    int dash = version.IndexOf('-');
                    if (dash <= 0) continue;
                    if (version.Substring(0, dash) != mc) continue;
                    string forgePart = version.Substring(dash + 1);
                    if (latest == null || CompareVersions(forgePart, latest) > 0) latest = forgePart;
                }
                if (latest == null) throw new Exception("Forge 没有适用于 Minecraft " + mc + " 的版本。");
                entry = new LoaderEntry { Type = "forge", McVersion = mc, Version = latest };
            }
            else if (type == "NeoForge")
            {
                string xml = Engine.GetString("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", "direct", null);
                string latest = null;
                foreach (string version in XmlVersions(xml))
                {
                    token.ThrowIfCancellationRequested();
                    if (version.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        version.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (NeoForgeMcVersion(version) != mc) continue;
                    if (latest == null || CompareVersions(version, latest) > 0) latest = version;
                }
                if (latest == null) throw new Exception("NeoForge 没有适用于 Minecraft " + mc + " 的版本。");
                entry = new LoaderEntry { Type = "neoforge", McVersion = mc, Version = latest };
            }
            else if (type == "Fabric")
            {
                string json = Engine.GetString("https://meta.fabricmc.net/v2/versions/loader", "direct", null);
                var array = Engine.ParseArray(json);
                if (array == null || array.Length == 0) throw new Exception("无法获取 Fabric 版本列表。");
                var first = Engine.Obj(array[0]);
                string version = Engine.Str(first.ContainsKey("version") ? first["version"] : null);
                if (string.IsNullOrEmpty(version)) throw new Exception("无法解析 Fabric 版本。");
                entry = new LoaderEntry { Type = "fabric", McVersion = mc, Version = version };
            }
            else
            {
                string json = Engine.GetString("https://bmclapi2.bangbang93.com/optifine/versionList", "direct", null);
                var array = Engine.ParseArray(json);
                if (array == null) throw new Exception("无法获取 OptiFine 版本列表。");
                string bestType = null;
                string bestPatch = null;
                foreach (object raw in array)
                {
                    token.ThrowIfCancellationRequested();
                    var item = Engine.Obj(raw);
                    if (item == null) continue;
                    string mcPart = Engine.Str(item.ContainsKey("mcversion") ? item["mcversion"] : null);
                    string typePart = Engine.Str(item.ContainsKey("type") ? item["type"] : null);
                    string patch = Engine.Str(item.ContainsKey("patch") ? item["patch"] : null);
                    if (mcPart != mc || string.IsNullOrEmpty(typePart) || string.IsNullOrEmpty(patch)) continue;
                    if (patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase)) continue;
                    if (bestPatch == null || CompareVersions(patch, bestPatch) > 0) { bestType = typePart; bestPatch = patch; }
                }
                if (bestPatch == null) throw new Exception("OptiFine 没有适用于 Minecraft " + mc + " 的正式版。");
                entry = new LoaderEntry
                {
                    Type = "optifine", McVersion = mc,
                    Version = mc + "_" + bestType + "_" + bestPatch,
                    ExtraType = bestType, ExtraPatch = bestPatch
                };
            }

            LogLine("一键安装：" + mc + " + " + entry);
            DoInstallLoader(entry, token);
        }

        private static int CompareVersions(string a, string b)
        {
            string[] pa = a.Split('.', '-', '_', '+');
            string[] pb = b.Split('.', '-', '_', '+');
            int count = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < count; i++)
            {
                int na = 0, nb = 0;
                if (i < pa.Length) int.TryParse(pa[i], out na);
                if (i < pb.Length) int.TryParse(pb[i], out nb);
                if (na != nb) return na.CompareTo(nb);
            }
            return string.CompareOrdinal(a, b);
        }

        private void InstallFabricLoader(string gameRoot, string mcVersion, string loaderVersion, string instanceName, IProgressSink sink, CancellationToken token)
        {
            string profileUrl = "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(mcVersion) + "/" +
                                Uri.EscapeDataString(loaderVersion) + "/profile/json";
            var profile = Engine.ParseObject(Engine.GetString(profileUrl, "direct", sink));
            var libraries = Engine.Arr(profile.ContainsKey("libraries") ? profile["libraries"] : null);
            if (libraries != null)
            {
                foreach (object rawLib in libraries)
                {
                    token.ThrowIfCancellationRequested();
                    var lib = Engine.Obj(rawLib);
                    if (lib == null) continue;
                    string coord = Engine.Str(lib.ContainsKey("name") ? lib["name"] : null);
                    if (string.IsNullOrEmpty(coord)) continue;
                    string baseUrl = lib.ContainsKey("url") ? Engine.Str(lib["url"]) : "https://maven.fabricmc.net/";
                    if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://maven.fabricmc.net/";
                    string relative = MavenRelative(coord);
                    string hash = Engine.Str(lib.ContainsKey("sha1") ? lib["sha1"] : null);
                    long size = Engine.Long(lib.ContainsKey("size") ? lib["size"] : null);
                    Report("下载 Fabric 库：" + coord);
                    Engine.Download(baseUrl.TrimEnd('/') + "/" + relative,
                        Engine.SafeChild(gameRoot, "libraries/" + relative), hash, "SHA1", size, "library", sink, token);
                }
            }
            string dir = Path.Combine(gameRoot, "versions", instanceName);
            Directory.CreateDirectory(dir);
            profile["id"] = instanceName;
            profile["inheritsFrom"] = mcVersion;
            WriteJsonFile(profile, Path.Combine(dir, instanceName + ".json"));
        }

        private static string NeoForgeMcVersion(string version)
        {
            string[] parts = version.Split('.');
            if (parts.Length < 2) return version;
            int major;
            if (!int.TryParse(parts[0], out major)) return version;
            if (major <= 21) return "1." + parts[0] + "." + parts[1];
            if (parts.Length >= 3 && parts[2] != "0") return parts[0] + "." + parts[1] + "." + parts[2];
            return parts[0] + "." + parts[1];
        }

        private static List<string> XmlVersions(string xml)
        {
            var result = new List<string>();
            var document = new XmlDocument();
            document.LoadXml(xml);
            XmlNodeList nodes = document.GetElementsByTagName("version");
            foreach (XmlNode node in nodes) result.Add(node.InnerText.Trim());
            return result;
        }

        private static string MakeInstanceName(string gameRoot, string displayName)
        {
            var builder = new StringBuilder();
            foreach (char c in displayName.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.') builder.Append(c);
            }
            string baseName = builder.ToString().Trim();
            if (baseName.Length == 0) baseName = "Modpack";
            if (baseName.Length > 40) baseName = baseName.Substring(0, 40).Trim();
            string versionsRoot = Path.Combine(gameRoot, "versions");
            string candidate = baseName;
            int suffix = 2;
            while (Directory.Exists(Path.Combine(versionsRoot, candidate)))
            {
                candidate = baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }
            return candidate;
        }

        private static string MavenRelative(string coord)
        {
            string[] parts = coord.Split(':');
            if (parts.Length < 3) throw new Exception("无法解析 Maven 坐标：" + coord);
            string group = parts[0].Replace('.', '/');
            string artifact = parts[1];
            string version = parts[2];
            return group + "/" + artifact + "/" + version + "/" + artifact + "-" + version + ".jar";
        }

        private static void WriteJsonFile(Dictionary<string, object> data, string path)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string text = serializer.Serialize(data);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            foreach (string directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }
    }
}
