using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
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
        private const string StartupTaskName = "Clash Verge Toolbar Widget";
        private const string SettingsKey = @"Software\ClashLeftWidget";
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer positionTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer menuDismissTimer = new System.Windows.Forms.Timer();
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
        private bool settingsOpen;
        private int horizontalOffset;
        private int verticalOffset;
        private int refreshSeconds;
        private string language;
        private DateTime menuOpenedAt;

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
            menu.AutoClose = true;
            menu.Opened += delegate { menuOpenedAt = DateTime.UtcNow; menuDismissTimer.Start(); };
            menu.Closed += delegate { menuDismissTimer.Stop(); };
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
            menuDismissTimer.Interval = 60;
            menuDismissTimer.Tick += delegate
            {
                if ((DateTime.UtcNow - menuOpenedAt).TotalMilliseconds < 250) return;
                bool pressed = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
                if (pressed && !menu.Bounds.Contains(Cursor.Position)) menu.Close();
            };
        }

        private void SyncWithClash()
        {
            if (IsClashRunning())
            {
                if (IsForegroundFullscreen())
                {
                    if (Visible) Hide();
                    return;
                }
                if (!Visible) Show();
                if (!settingsOpen)
                {
                    PlaceOnTaskbar();
                    KeepAboveWindows();
                }
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

        private bool IsForegroundFullscreen()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == Handle) return false;
            var className = new StringBuilder(128);
            GetClassName(foreground, className, className.Capacity);
            string windowClass = className.ToString();
            if (windowClass == "Progman" || windowClass == "WorkerW" || windowClass == "Shell_TrayWnd") return false;
            RECT windowRect;
            if (DwmGetWindowAttribute(foreground, 9, out windowRect, Marshal.SizeOf(typeof(RECT))) != 0 &&
                !GetWindowRect(foreground, out windowRect)) return false;
            IntPtr monitor = MonitorFromWindow(foreground, 2);
            if (monitor == IntPtr.Zero) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            const int margin = 3;
            return windowRect.left <= info.rcMonitor.left + margin &&
                   windowRect.top <= info.rcMonitor.top + margin &&
                   windowRect.right >= info.rcMonitor.right - margin &&
                   windowRect.bottom >= info.rcMonitor.bottom - margin;
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
                settingsOpen = true;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    settingsOpen = false;
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
                settingsOpen = false;
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

        private static bool IsStartupEnabled()
        {
            if (RunSchtasks("/Query /TN \"" + StartupTaskName + "\"")) return true;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunValue) != null;
        }

        private static void SetStartup(bool enabled)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValue, false);
            }
            if (enabled)
            {
                string taskCommand = "\\\"" + Application.ExecutablePath + "\\\"";
                RunSchtasks("/Create /TN \"" + StartupTaskName + "\" /TR \"" + taskCommand + "\" /SC ONLOGON /DELAY 0000:08 /RL LIMITED /F");
            }
            else RunSchtasks("/Delete /TN \"" + StartupTaskName + "\" /F");
        }

        private static bool RunSchtasks(string arguments)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    process.WaitForExit(5000);
                    return process.HasExited && process.ExitCode == 0;
                }
            }
            catch { return false; }
        }
        private static int ReadSetting(string name, int fallback, int min, int max) { using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey)) { int value; if (k != null && int.TryParse(Convert.ToString(k.GetValue(name)), out value)) return Math.Max(min, Math.Min(max, value)); } return fallback; }
        private static void WriteSetting(string name, int value) { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.DWord); }
        private static string ReadTextSetting(string name, string fallback) { using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey)) { var value = k == null ? null : k.GetValue(name); return value == null ? fallback : Convert.ToString(value); } }
        private static void WriteTextSetting(string name, string value) { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.String); }

        private sealed class Resolved { public string NodeName; public List<string> Chain; public int Delay; public bool Alive; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public int lParam; }
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TrackBar offset = new TrackBar();
        private readonly Label offsetValue = new Label();
        private readonly TrackBar verticalOffset = new TrackBar();
        private readonly Label verticalOffsetValue = new Label();
        private readonly NumericUpDown refresh = new NumericUpDown();
        private readonly CheckBox startup = new CheckBox();
        private readonly Label startupHint = new Label();
        private readonly ComboBox language = new ComboBox();
        private readonly Action<int, int> previewPosition;
        private readonly Label title = new Label();
        private readonly GroupBox positionGroup = new GroupBox();
        private readonly Label horizontalLabel = new Label();
        private readonly Label verticalLabel = new Label();
        private readonly GroupBox generalGroup = new GroupBox();
        private readonly Label refreshLabel = new Label();
        private readonly Label secondsLabel = new Label();
        private readonly Label languageLabel = new Label();
        private readonly Button defaults = new Button();
        private readonly Button cancel = new Button();
        private readonly Button save = new Button();

        public int HorizontalOffset { get { return offset.Value; } }
        public int VerticalOffset { get { return verticalOffset.Value; } }
        public int RefreshSeconds { get { return Decimal.ToInt32(refresh.Value); } }
        public bool StartWithWindows { get { return startup.Checked; } }
        public string Language { get { return language.SelectedIndex == 1 ? "en" : "zh"; } }

        public SettingsForm(int horizontalOffset, int currentVerticalOffset, int refreshSeconds, bool startWithWindows, string currentLanguage, Action<int, int> positionPreview)
        {
            previewPosition = positionPreview;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(580, 550);
            MinimumSize = new Size(596, 589);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(246, 248, 252);

            title.Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(30, 45, 70); title.AutoSize = false;
            title.Location = new Point(24, 14); title.Size = new Size(532, 46); title.TextAlign = ContentAlignment.MiddleLeft;

            positionGroup.Location = new Point(20, 70); positionGroup.Size = new Size(540, 225);
            positionGroup.BackColor = Color.White; positionGroup.ForeColor = Color.FromArgb(55, 68, 90);
            horizontalLabel.Location = new Point(18, 32); horizontalLabel.Size = new Size(410, 24);
            offset.Minimum = -500; offset.Maximum = 500; offset.SmallChange = 5; offset.LargeChange = 25; offset.TickFrequency = 50;
            offset.Value = Math.Max(-500, Math.Min(500, horizontalOffset)); offset.Location = new Point(14, 58); offset.Size = new Size(445, 45);
            offsetValue.Location = new Point(466, 64); offsetValue.Size = new Size(62, 24); offsetValue.TextAlign = ContentAlignment.MiddleRight; offsetValue.Text = FormatOffset(offset.Value);
            offset.Scroll += delegate { offsetValue.Text = FormatOffset(offset.Value); Preview(); };

            verticalLabel.Location = new Point(18, 122); verticalLabel.Size = new Size(410, 24);
            verticalOffset.Minimum = -30; verticalOffset.Maximum = 30; verticalOffset.SmallChange = 1; verticalOffset.LargeChange = 5; verticalOffset.TickFrequency = 5;
            verticalOffset.Value = Math.Max(-30, Math.Min(30, currentVerticalOffset)); verticalOffset.Location = new Point(14, 148); verticalOffset.Size = new Size(445, 45);
            verticalOffsetValue.Location = new Point(466, 154); verticalOffsetValue.Size = new Size(62, 24); verticalOffsetValue.TextAlign = ContentAlignment.MiddleRight; verticalOffsetValue.Text = FormatOffset(verticalOffset.Value);
            verticalOffset.Scroll += delegate { verticalOffsetValue.Text = FormatOffset(verticalOffset.Value); Preview(); };
            positionGroup.Controls.AddRange(new Control[] { horizontalLabel, offset, offsetValue, verticalLabel, verticalOffset, verticalOffsetValue });

            generalGroup.Location = new Point(20, 310); generalGroup.Size = new Size(540, 165);
            generalGroup.BackColor = Color.White; generalGroup.ForeColor = Color.FromArgb(55, 68, 90);
            refreshLabel.Location = new Point(18, 32); refreshLabel.Size = new Size(190, 26);
            refresh.Minimum = 2; refresh.Maximum = 60; refresh.Value = Math.Max(2, Math.Min(60, refreshSeconds));
            refresh.Location = new Point(210, 29); refresh.Size = new Size(90, 26);
            secondsLabel.Location = new Point(308, 32); secondsLabel.Size = new Size(90, 26); secondsLabel.ForeColor = Color.DimGray;

            languageLabel.Location = new Point(18, 70); languageLabel.Size = new Size(190, 26);
            language.DropDownStyle = ComboBoxStyle.DropDownList; language.Items.AddRange(new object[] { "中文 / Chinese", "English / 英文" });
            language.Location = new Point(210, 67); language.Size = new Size(230, 28); language.DropDownWidth = 230;

            startup.AutoSize = false; startup.Checked = startWithWindows; startup.Location = new Point(18, 101); startup.Size = new Size(505, 27);
            startupHint.Location = new Point(42, 128); startupHint.Size = new Size(480, 24); startupHint.ForeColor = Color.FromArgb(105, 115, 132);
            generalGroup.Controls.AddRange(new Control[] { refreshLabel, refresh, secondsLabel, languageLabel, language, startup, startupHint });

            StyleButton(defaults, false); defaults.Location = new Point(20, 496); defaults.Size = new Size(145, 36);
            defaults.Click += delegate { offset.Value = 0; verticalOffset.Value = 0; offsetValue.Text = FormatOffset(0); verticalOffsetValue.Text = FormatOffset(0); Preview(); };
            StyleButton(cancel, false); cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(382, 496); cancel.Size = new Size(82, 36);
            StyleButton(save, true); save.DialogResult = DialogResult.OK; save.Location = new Point(474, 496); save.Size = new Size(86, 36);
            AcceptButton = save; CancelButton = cancel;
            Controls.AddRange(new Control[] { title, positionGroup, generalGroup, defaults, cancel, save });

            language.SelectedIndex = currentLanguage == "en" ? 1 : 0;
            language.SelectedIndexChanged += delegate { ApplyLanguage(); };
            ApplyLanguage();
        }

        private void Preview() { if (previewPosition != null) previewPosition(offset.Value, verticalOffset.Value); }
        private static string FormatOffset(int value) { return (value > 0 ? "+" : "") + value + " px"; }

        private void ApplyLanguage()
        {
            bool en = Language == "en";
            Text = en ? "Clash Verge Toolbar Widget — Settings" : "Clash Verge Toolbar Widget — 设置";
            title.Text = en ? "Display settings" : "显示设置";
            positionGroup.Text = en ? " Position " : " 位置 ";
            horizontalLabel.Text = en ? "Horizontal position · live preview" : "水平位置 · 实时预览";
            verticalLabel.Text = en ? "Vertical position · live preview" : "垂直位置 · 实时预览";
            generalGroup.Text = en ? " General " : " 常规 ";
            refreshLabel.Text = en ? "Status refresh interval" : "状态刷新间隔";
            secondsLabel.Text = en ? "seconds" : "秒";
            languageLabel.Text = "语言 / Language";
            startup.Text = en ? "Start with Windows" : "随 Windows 启动";
            startupHint.Text = en ? "Automatically hides while Clash is not running." : "Clash 未运行时自动隐藏。";
            defaults.Text = en ? "Reset position" : "恢复默认位置";
            cancel.Text = en ? "Cancel" : "取消";
            save.Text = en ? "Save" : "保存";
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(185, 194, 210);
            button.BackColor = primary ? Color.FromArgb(32, 120, 245) : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(45, 58, 78);
            button.Cursor = Cursors.Hand;
        }
    }
}
