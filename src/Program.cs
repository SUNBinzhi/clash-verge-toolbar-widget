using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClashLeftWidget
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\ClashVergeToolbarWidget-v2", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
            }
        }
    }

    internal sealed class WidgetForm : Form
    {
        private const string RootGroup = "🚀 节点选择";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "ClashLeftWidget";
        private const string SettingsKey = @"Software\ClashLeftWidget";
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer positionTimer = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolTip tip = new ToolTip();
        private readonly ToolStripMenuItem statusMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem refreshMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem settingsMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem startupMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem exitMenuItem = new ToolStripMenuItem();
        private string region = "OFF";
        private string shortNode = "Clash 离线";
        private string delay = "--";
        private string detail = "Clash 未运行";
        private Color statusColor = Color.FromArgb(125, 132, 145);
        private bool polling;
        private int horizontalOffset;
        private int verticalOffset;
        private int refreshSeconds;
        private string language;

        public WidgetForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Width = 190;
            Height = 40;
            BackColor = Color.FromArgb(1, 2, 3);
            TransparencyKey = BackColor;
            DoubleBuffered = true;
            Opacity = 1.0;
            Cursor = Cursors.Hand;
            Text = "Clash 节点状态";
            horizontalOffset = ReadSetting("HorizontalOffset", 0, -1000, 1000);
            verticalOffset = ReadSetting("VerticalOffset", 0, -100, 100);
            refreshSeconds = ReadSetting("RefreshSeconds", 5, 2, 60);
            language = ReadTextSetting("Language", "zh") == "en" ? "en" : "zh";

            statusMenuItem.Enabled = false; statusMenuItem.Name = "status";
            refreshMenuItem.Click += async delegate { await PollAsync(); };
            settingsMenuItem.Click += delegate { ShowSettings(); };
            startupMenuItem.Checked = IsStartupEnabled(); startupMenuItem.CheckOnClick = true;
            startupMenuItem.CheckedChanged += delegate { SetStartup(startupMenuItem.Checked); };
            exitMenuItem.Click += delegate { Close(); };
            menu.Items.Add(statusMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(refreshMenuItem);
            menu.Items.Add(settingsMenuItem);
            menu.Items.Add(startupMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitMenuItem);
            ContextMenuStrip = menu;
            ApplyLanguage();

            MouseClick += async delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) await PollAsync();
            };

            Shown += delegate { SyncWithClash(); };
            refreshTimer.Interval = refreshSeconds * 1000;
            refreshTimer.Tick += async delegate { if (IsClashRunning()) await PollAsync(); };
            refreshTimer.Start();
            positionTimer.Interval = 1000;
            positionTimer.Tick += delegate { SyncWithClash(); };
            positionTimer.Start();
        }

        private void SyncWithClash()
        {
            if (IsClashRunning())
            {
                if (!Visible) Show();
                PlaceOnTaskbar();
                KeepAboveWindows();
                if (shortNode == "Clash 离线" || shortNode == "离线")
                {
                    Task ignored = PollAsync();
                }
            }
            else if (Visible)
            {
                Hide();
                region = "OFF";
                shortNode = "Clash 离线";
                delay = "--";
            }
        }

        private static bool IsClashRunning()
        {
            return Process.GetProcessesByName("clash-verge").Length > 0;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x80;
                const int WS_EX_NOACTIVATE = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawFlag(g, region, new Rectangle(2, 6, 42, 28));
            using (var dot = new SolidBrush(statusColor)) g.FillEllipse(dot, 50, 16, 8, 8);
            using (var primary = new SolidBrush(Color.FromArgb(242, 244, 248)))
            using (var delayBrush = new SolidBrush(statusColor))
            using (var mainFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            using (var delayFont = new Font("Segoe UI Semibold", 9f, FontStyle.Bold, GraphicsUnit.Point))
            {
                DrawShadowText(g, shortNode, mainFont, primary, new RectangleF(64, 5, 65, 27), CenterLeft());
                DrawShadowText(g, delay + (delay == "--" ? "" : " ms"), delayFont, delayBrush,
                    new RectangleF(127, 5, 69, 27), CenterLeft());
            }
        }

        private async Task PollAsync()
        {
            if (polling) return;
            polling = true;
            try
            {
                string json = await PipeHttp.GetAsync("verge-mihomo", "/proxies", 2500);
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                var proxies = root != null && root.ContainsKey("proxies") ? root["proxies"] as Dictionary<string, object> : null;
                if (proxies == null) throw new InvalidDataException("未返回节点列表");
                var result = Resolve(proxies, RootGroup);
                region = RegionCode(result.NodeName);
                shortNode = NodeAbbreviation(result.NodeName, region);
                delay = result.Delay > 0 ? result.Delay.ToString() : "--";
                statusColor = StatusColor(result.Alive, result.Delay);
                detail = result.NodeName + "\n" + (result.Delay > 0 ? result.Delay + " ms" : "暂无延迟") + "\n" + string.Join(" > ", result.Chain.ToArray());
            }
            catch (Exception ex)
            {
                region = "OFF"; shortNode = "离线"; delay = "--"; statusColor = Color.FromArgb(125, 132, 145); detail = "Clash 离线\n" + ex.Message;
            }
            finally
            {
                tip.SetToolTip(this, detail);
                statusMenuItem.Text = detail.Replace("\n", "  ");
                Invalidate();
                polling = false;
            }
        }

        private void PlaceOnTaskbar()
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
            if (SHAppBarMessage(5, ref data) == IntPtr.Zero) return;
            int taskHeight = data.rc.bottom - data.rc.top;
            int taskWidth = data.rc.right - data.rc.left;
            if (taskWidth >= taskHeight)
            {
                Height = Math.Max(34, Math.Min(42, taskHeight - 8));
                Width = 190;
                int liteRight = FindLiteMonitorRight();
                float scale;
                using (var graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
                int defaultLeft = liteRight > 0 ? (int)Math.Round((liteRight + 12) * Math.Max(1f, scale)) : data.rc.left + 180;
                Left = Math.Max(data.rc.left, Math.Min(data.rc.right - Width, defaultLeft + horizontalOffset));
                Top = data.rc.top + (taskHeight - Height) / 2 + verticalOffset;
            }
            else
            {
                Width = Math.Max(42, taskWidth - 8);
                Height = 120;
                Left = data.rc.left + (taskWidth - Width) / 2;
                Top = data.rc.top + 10;
            }
        }

        private void KeepAboveWindows()
        {
            if (!IsHandleCreated || !Visible) return;
            SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private void ShowSettings()
        {
            int originalHorizontalOffset = horizontalOffset;
            int originalVerticalOffset = verticalOffset;
            using (var dialog = new SettingsForm(horizontalOffset, verticalOffset, refreshSeconds, IsStartupEnabled(), language,
                delegate(int horizontal, int vertical) { horizontalOffset = horizontal; verticalOffset = vertical; PlaceOnTaskbar(); KeepAboveWindows(); }))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    horizontalOffset = originalHorizontalOffset;
                    verticalOffset = originalVerticalOffset;
                    PlaceOnTaskbar(); KeepAboveWindows();
                    return;
                }
                horizontalOffset = dialog.HorizontalOffset;
                verticalOffset = dialog.VerticalOffset;
                refreshSeconds = dialog.RefreshSeconds;
                language = dialog.Language;
                refreshTimer.Interval = refreshSeconds * 1000;
                SetStartup(dialog.StartWithWindows);
                WriteSetting("HorizontalOffset", horizontalOffset);
                WriteSetting("VerticalOffset", verticalOffset);
                WriteSetting("RefreshSeconds", refreshSeconds);
                WriteTextSetting("Language", language);
                startupMenuItem.Checked = dialog.StartWithWindows;
                ApplyLanguage();
                PlaceOnTaskbar();
                KeepAboveWindows();
            }
        }

        private void ApplyLanguage()
        {
            bool en = language == "en";
            Text = en ? "Clash node status" : "Clash 节点状态";
            refreshMenuItem.Text = en ? "Refresh now" : "立即刷新";
            settingsMenuItem.Text = en ? "Settings…" : "设置…";
            startupMenuItem.Text = en ? "Start with Windows" : "开机自启";
            exitMenuItem.Text = en ? "Exit" : "退出";
            if (!polling) statusMenuItem.Text = detail.Replace("\n", "  ");
        }

        private static Resolved Resolve(Dictionary<string, object> proxies, string start)
        {
            string name = start; var chain = new List<string>(); var seen = new HashSet<string>(); Dictionary<string, object> item = null;
            while (seen.Add(name) && proxies.ContainsKey(name))
            {
                chain.Add(name); item = proxies[name] as Dictionary<string, object>;
                if (item == null || !item.ContainsKey("now")) break;
                string next = Convert.ToString(item["now"]); if (string.IsNullOrEmpty(next) || next == name) break; name = next;
            }
            if (item == null) throw new InvalidDataException("找不到策略组");
            int lastDelay = 0;
            var history = item.ContainsKey("history") ? item["history"] as object[] : null;
            if (history != null) foreach (var h in history) { var d = h as Dictionary<string, object>; int v; if (d != null && d.ContainsKey("delay") && int.TryParse(Convert.ToString(d["delay"]), out v) && v > 0) lastDelay = v; }
            return new Resolved { NodeName = name, Chain = chain, Delay = lastDelay, Alive = !item.ContainsKey("alive") || Convert.ToBoolean(item["alive"]) };
        }

        private static string NodeAbbreviation(string name, string region)
        {
            var m = Regex.Match(name, @"(?:香港|深港|广台|新坡|广新|日本|沪日|美国|沪美|韩国|沪韩)([A-Z]?\d{1,2})", RegexOptions.IgnoreCase);
            return m.Success ? region + m.Groups[1].Value.ToUpperInvariant() : region;
        }

        private static string RegionCode(string n)
        {
            if (Has(n, "🇭🇰", "香港", "深港", "Hong Kong")) return "HK"; if (Has(n, "台湾", "广台", "Taiwan")) return "TW";
            if (Has(n, "🇨🇳", "中国", "大陆", "China")) return "CN";
            if (Has(n, "🇸🇬", "新加坡", "新坡", "广新")) return "SG"; if (Has(n, "🇯🇵", "日本", "沪日")) return "JP";
            if (Has(n, "🇺🇲", "🇺🇸", "美国", "沪美", "美西", "美东", "United States")) return "US"; if (Has(n, "🇰🇷", "韩国", "沪韩", "Korea")) return "KR";
            if (Has(n, "🇬🇧", "英国", "United Kingdom", "London")) return "GB"; if (Has(n, "🇩🇪", "德国", "Germany")) return "DE";
            if (Has(n, "🇫🇷", "法国", "France")) return "FR"; if (Has(n, "🇳🇱", "荷兰", "Netherlands")) return "NL";
            if (Has(n, "🇨🇦", "加拿大", "Canada")) return "CA"; if (Has(n, "🇦🇺", "澳大利亚", "澳洲", "Australia")) return "AU";
            if (Has(n, "🇮🇳", "印度", "India")) return "IN"; if (Has(n, "🇷🇺", "俄罗斯", "Russia")) return "RU";
            if (Has(n, "🇹🇷", "土耳其", "Turkey")) return "TR"; if (Has(n, "🇹🇭", "泰国", "Thailand")) return "TH";
            if (Has(n, "🇲🇾", "马来西亚", "Malaysia")) return "MY"; if (Has(n, "🇵🇭", "菲律宾", "Philippines")) return "PH";
            if (Has(n, "🇮🇩", "印度尼西亚", "印尼", "Indonesia")) return "ID"; if (Has(n, "🇻🇳", "越南", "Vietnam")) return "VN";
            if (Has(n, "🇧🇷", "巴西", "Brazil")) return "BR"; if (Has(n, "🇦🇪", "阿联酋", "UAE", "Dubai")) return "AE";
            if (Has(n, "🇸🇦", "沙特", "Saudi")) return "SA"; if (Has(n, "🇨🇭", "瑞士", "Switzerland")) return "CH";
            if (Has(n, "🇸🇪", "瑞典", "Sweden")) return "SE"; if (Has(n, "🇳🇴", "挪威", "Norway")) return "NO";
            if (Has(n, "🇫🇮", "芬兰", "Finland")) return "FI"; if (Has(n, "🇮🇹", "意大利", "Italy")) return "IT";
            if (Has(n, "🇪🇸", "西班牙", "Spain")) return "ES"; if (Has(n, "🇵🇹", "葡萄牙", "Portugal")) return "PT";
            if (Has(n, "🇵🇱", "波兰", "Poland")) return "PL"; return "PX";
        }

        private static bool Has(string v, params string[] x) { return x.Any(s => v.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0); }
        private static Color StatusColor(bool alive, int d) { return !alive ? Color.FromArgb(230, 75, 75) : d <= 0 ? Color.Gray : d < 180 ? Color.FromArgb(55, 205, 120) : d < 350 ? Color.FromArgb(250, 180, 60) : Color.FromArgb(240, 90, 65); }
        private static StringFormat CenterLeft() { return new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center }; }
        private static StringFormat CenterRight() { return new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center }; }
        private static GraphicsPath RoundedRect(Rectangle r, int radius) { var p = new GraphicsPath(); int d = radius * 2; p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }

        private static void DrawFlag(Graphics g, string code, Rectangle r)
        {
            var embedded = LoadEmbeddedFlag(code);
            if (embedded != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                float scale = Math.Min((float)r.Width / embedded.Width, (float)r.Height / embedded.Height);
                int width = Math.Max(1, (int)Math.Round(embedded.Width * scale));
                int height = Math.Max(1, (int)Math.Round(embedded.Height * scale));
                int x = r.X + (r.Width - width) / 2;
                int y = r.Y + (r.Height - height) / 2;
                g.DrawImage(embedded, new Rectangle(x, y, width, height));
                return;
            }
            if (code == "HK") { using (var b = new SolidBrush(Color.FromArgb(222, 35, 50))) g.FillRectangle(b, r); DrawHongKongFlower(g, r); }
            else if (code == "TW" || code == "CN") { using (var red = new SolidBrush(Color.FromArgb(222, 41, 16))) g.FillRectangle(red, r); using (var yellow = new SolidBrush(Color.FromArgb(255, 222, 0))) g.FillEllipse(yellow, r.X + 5, r.Y + 5, 7, 7); }
            else if (code == "SG") { g.FillRectangle(Brushes.White, r); g.FillRectangle(Brushes.Red, r.X, r.Y, r.Width, r.Height / 2); }
            else if (code == "JP") { g.FillRectangle(Brushes.White, r); using (var b = new SolidBrush(Color.Crimson)) g.FillEllipse(b, r.X + 10, r.Y + 5, 12, 12); }
            else if (code == "US") { g.FillRectangle(Brushes.White, r); using (var red = new SolidBrush(Color.Firebrick)) for (int i = 0; i < 4; i++) g.FillRectangle(red, r.X, r.Y + i * 6, r.Width, 3); using (var blue = new SolidBrush(Color.Navy)) g.FillRectangle(blue, r.X, r.Y, 14, 11); }
            else if (code == "KR") { g.FillRectangle(Brushes.White, r); using (var red = new SolidBrush(Color.Crimson)) g.FillPie(red, r.X + 10, r.Y + 5, 12, 12, 180, 180); using (var blue = new SolidBrush(Color.RoyalBlue)) g.FillPie(blue, r.X + 10, r.Y + 5, 12, 12, 0, 180); }
            else { using (var b = new SolidBrush(Color.FromArgb(70, 82, 105))) g.FillRectangle(b, r); }
        }

        private static readonly Dictionary<string, Image> FlagCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        private static Image LoadEmbeddedFlag(string code)
        {
            Image cached;
            if (FlagCache.TryGetValue(code, out cached)) return cached;
            string key = code.ToLowerInvariant();
            if (key == "tw") key = "cn";
            if (!Regex.IsMatch(key, "^[a-z]{2}$")) return null;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("flags." + key + ".png"))
            {
                if (stream == null) return null;
                using (var source = Image.FromStream(stream)) cached = new Bitmap(source);
            }
            FlagCache[code] = cached;
            return cached;
        }

        private static void DrawHongKongFlower(Graphics g, Rectangle r)
        {
            var state = g.Save();
            g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
            using (var white = new SolidBrush(Color.White))
            using (var red = new Pen(Color.FromArgb(222, 35, 50), 0.8f))
            {
                for (int i = 0; i < 5; i++)
                {
                    g.RotateTransform(72f);
                    var petal = new GraphicsPath();
                    petal.AddBezier(0, -1, 2, -10, 11, -9, 8, -2);
                    petal.AddBezier(8, -2, 5, 3, 1, 4, 0, -1);
                    g.FillPath(white, petal);
                    g.DrawArc(red, 2, -7, 5, 7, 205, 105);
                    petal.Dispose();
                }
            }
            g.Restore(state);
        }

        private static void DrawShadowText(Graphics g, string text, Font font, Brush foreground, RectangleF rect, StringFormat format)
        {
            using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                var shifted = rect; shifted.X += 1; shifted.Y += 1;
                g.DrawString(text, font, shadow, shifted, format);
            }
            g.DrawString(text, font, foreground, rect, format);
            format.Dispose();
        }

        private static int FindLiteMonitorRight()
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName("LiteMonitor").Select(p => (uint)p.Id));
            if (pids.Count == 0) return 0;
            int best = 0;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                RECT rect; if (!GetWindowRect(h, out rect)) return true;
                int width = rect.right - rect.left; int height = rect.bottom - rect.top;
                if (rect.left <= 20 && rect.top <= 100 && width >= 50 && width <= 600 && height >= 20 && height <= 150)
                    best = Math.Max(best, rect.right);
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static bool IsStartupEnabled() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunValue) != null; }
        private static void SetStartup(bool enabled) { using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) { if (enabled) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\""); else k.DeleteValue(RunValue, false); } }
        private static int ReadSetting(string name, int fallback, int min, int max) { using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey)) { int value; if (k != null && int.TryParse(Convert.ToString(k.GetValue(name)), out value)) return Math.Max(min, Math.Min(max, value)); } return fallback; }
        private static void WriteSetting(string name, int value) { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.DWord); }
        private static string ReadTextSetting(string name, string fallback) { using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey)) { var value = k == null ? null : k.GetValue(name); return value == null ? fallback : Convert.ToString(value); } }
        private static void WriteTextSetting(string name, string value) { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.String); }

        private sealed class Resolved { public string NodeName; public List<string> Chain; public int Delay; public bool Alive; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public int lParam; }
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TrackBar offset = new TrackBar();
        private readonly Label offsetValue = new Label();
        private readonly TrackBar verticalOffset = new TrackBar();
        private readonly Label verticalOffsetValue = new Label();
        private readonly NumericUpDown refresh = new NumericUpDown();
        private readonly CheckBox startup = new CheckBox();
        private readonly ComboBox language = new ComboBox();
        private readonly Action<int, int> previewPosition;

        public int HorizontalOffset { get { return offset.Value; } }
        public int VerticalOffset { get { return verticalOffset.Value; } }
        public int RefreshSeconds { get { return Decimal.ToInt32(refresh.Value); } }
        public bool StartWithWindows { get { return startup.Checked; } }
        public string Language { get { return language.SelectedIndex == 1 ? "en" : "zh"; } }

        public SettingsForm(int horizontalOffset, int currentVerticalOffset, int refreshSeconds, bool startWithWindows, string currentLanguage, Action<int, int> positionPreview)
        {
            previewPosition = positionPreview;
            bool en = currentLanguage == "en";
            Text = en ? "Clash node display settings" : "Clash 节点显示设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 445);
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;

            var title = new Label { Text = en ? "Display settings" : "显示设置", Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold), AutoSize = true, Location = new Point(22, 18) };
            var positionLabel = new Label { Text = en ? "Horizontal position (live preview)" : "水平位置（拖动时实时预览）", AutoSize = true, Location = new Point(24, 60) };
            offset.Minimum = -500; offset.Maximum = 500; offset.SmallChange = 5; offset.LargeChange = 25; offset.TickFrequency = 50;
            offset.Value = Math.Max(-500, Math.Min(500, horizontalOffset)); offset.Location = new Point(20, 82); offset.Width = 390;
            offsetValue.AutoSize = true; offsetValue.Location = new Point(416, 88); offsetValue.Text = FormatOffset(offset.Value);
            offset.Scroll += delegate { offsetValue.Text = FormatOffset(offset.Value); Preview(); };

            var verticalLabel = new Label { Text = en ? "Vertical position (live preview)" : "垂直位置（拖动时实时预览）", AutoSize = true, Location = new Point(24, 180) };
            verticalOffset.Minimum = -30; verticalOffset.Maximum = 30; verticalOffset.SmallChange = 1; verticalOffset.LargeChange = 5; verticalOffset.TickFrequency = 5;
            verticalOffset.Value = Math.Max(-30, Math.Min(30, currentVerticalOffset)); verticalOffset.Location = new Point(20, 202); verticalOffset.Width = 390;
            verticalOffsetValue.AutoSize = true; verticalOffsetValue.Location = new Point(416, 208); verticalOffsetValue.Text = FormatOffset(verticalOffset.Value);
            verticalOffset.Scroll += delegate { verticalOffsetValue.Text = FormatOffset(verticalOffset.Value); Preview(); };

            var refreshLabel = new Label { Text = en ? "Refresh interval" : "状态刷新间隔", AutoSize = true, Location = new Point(24, 270) };
            refresh.Minimum = 2; refresh.Maximum = 60; refresh.Value = Math.Max(2, Math.Min(60, refreshSeconds));
            refresh.Location = new Point(170, 266); refresh.Width = 105;
            var seconds = new Label { Text = en ? "seconds" : "秒", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(283, 270) };

            var languageLabel = new Label { Text = en ? "Language" : "界面语言", AutoSize = true, Location = new Point(24, 311) };
            language.DropDownStyle = ComboBoxStyle.DropDownList; language.Items.AddRange(new object[] { "中文", "English" });
            language.SelectedIndex = currentLanguage == "en" ? 1 : 0; language.Location = new Point(170, 307); language.Width = 105;

            startup.Text = en ? "Start with Windows (hide while Clash is not running)" : "随 Windows 启动（Clash 未运行时自动隐藏）";
            startup.AutoSize = true; startup.Checked = startWithWindows; startup.Location = new Point(24, 350);

            var defaults = new Button { Text = en ? "Reset position" : "恢复默认位置", AutoSize = true, Location = new Point(24, 395) };
            defaults.Click += delegate { offset.Value = 0; verticalOffset.Value = 0; offsetValue.Text = FormatOffset(0); verticalOffsetValue.Text = FormatOffset(0); Preview(); };
            var cancel = new Button { Text = en ? "Cancel" : "取消", DialogResult = DialogResult.Cancel, Size = new Size(75, 28), Location = new Point(312, 393) };
            var save = new Button { Text = en ? "Save" : "保存", DialogResult = DialogResult.OK, Size = new Size(75, 28), Location = new Point(393, 393) };
            AcceptButton = save; CancelButton = cancel;
            Controls.AddRange(new Control[] { title, positionLabel, offset, offsetValue, verticalLabel, verticalOffset, verticalOffsetValue, refreshLabel, refresh, seconds, languageLabel, language, startup, defaults, cancel, save });
        }

        private void Preview() { if (previewPosition != null) previewPosition(offset.Value, verticalOffset.Value); }
        private static string FormatOffset(int value) { return (value > 0 ? "+" : "") + value + " px"; }
    }
}
