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
            using (var mutex = new Mutex(true, "Local\\ClashLeftWidget-MeCloud", out created))
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
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer positionTimer = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolTip tip = new ToolTip();
        private string region = "OFF";
        private string shortNode = "Clash 离线";
        private string delay = "--";
        private string detail = "Clash 未运行";
        private Color statusColor = Color.FromArgb(125, 132, 145);
        private bool polling;

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

            var status = new ToolStripMenuItem("正在读取状态…") { Enabled = false, Name = "status" };
            var refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += async delegate { await PollAsync(); };
            var startup = new ToolStripMenuItem("开机自启") { Checked = IsStartupEnabled(), CheckOnClick = true };
            startup.CheckedChanged += delegate { SetStartup(startup.Checked); };
            var exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { Close(); };
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(refresh);
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            ContextMenuStrip = menu;

            MouseClick += async delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) await PollAsync();
            };

            Shown += delegate { SyncWithClash(); };
            refreshTimer.Interval = 5000;
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
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

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
                var item = menu.Items["status"] as ToolStripMenuItem;
                if (item != null) item.Text = detail.Replace("\n", "  ");
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
                Left = liteRight > 0 ? (int)Math.Round((liteRight + 12) * Math.Max(1f, scale)) : data.rc.left + 180;
                Top = data.rc.top + (taskHeight - Height) / 2;
            }
            else
            {
                Width = Math.Max(42, taskWidth - 8);
                Height = 120;
                Left = data.rc.left + (taskWidth - Width) / 2;
                Top = data.rc.top + 10;
            }
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
            if (Has(n, "🇭🇰", "香港", "深港")) return "HK"; if (Has(n, "🇨🇳", "台湾", "广台")) return "TW";
            if (Has(n, "🇸🇬", "新加坡", "新坡", "广新")) return "SG"; if (Has(n, "🇯🇵", "日本", "沪日")) return "JP";
            if (Has(n, "🇺🇲", "🇺🇸", "美国", "沪美", "美西", "美东")) return "US"; if (Has(n, "🇰🇷", "韩国", "沪韩")) return "KR"; return "PX";
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
            else if (code == "TW") { using (var red = new SolidBrush(Color.FromArgb(220, 30, 45))) g.FillRectangle(red, r); using (var blue = new SolidBrush(Color.Navy)) g.FillRectangle(blue, r.X, r.Y, r.Width / 2, r.Height / 2); using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, r.X + 5, r.Y + 3, 5, 5); }
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
            if (key != "hk" && key != "tw" && key != "sg" && key != "jp" && key != "us" && key != "kr") return null;
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

        private sealed class Resolved { public string NodeName; public List<string> Chain; public int Delay; public bool Alive; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public int lParam; }
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }
}
