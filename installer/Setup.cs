// FilesCompanionSetup.exe - the all-in-one installer for the Files companion set.
//
// Two components, one file, no network access required:
//
//   1. The launch shim (FilesOpen.exe). Files stays resident in the background
//      for speed, so opening a folder only *activates* the existing process and
//      Windows skips the startup animation. The shim starts Files exactly the
//      same way and plays a short animation on top.
//   2. Modern Recycle Bin - the Recycle Bin replacement, embedded in this very
//      installer.
//
// Everything lives in HKEY_CURRENT_USER: no administrator rights, no Explorer
// restart, and uninstalling restores the defaults.
//
// Safety rules learned the hard way and applied here:
//   * The uninstaller lives NEXT TO the install folder, never inside it.
//   * Uninstall removes a registry value ONLY when it still points at this exact
//     install, so a configuration set up by hand is never clobbered.
//   * The embedded Recycle Bin is only removed on uninstall when THIS installer
//     put it there (tracked with a marker file).
//
// The interface is drawn entirely with GDI+ on top of a DPI-aware, borderless
// window. Stock WinForms controls were the reason the first version looked dated
// and blurry at 125% scaling: the process was not DPI aware, so Windows scaled
// the finished bitmap and every glyph turned soft.
//
// .NET Framework only, compiled with the in-box csc (C# 5 syntax).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    internal const string AppName = "Files Companion";
    internal const string Version = "1.2.0";
    internal const string RecycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";

    internal static bool Silent;

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main(string[] args)
    {
        bool uninstall = Has(args, "--uninstall");
        bool silent = Has(args, "--silent") || Has(args, "/S");
        bool noRouting = Has(args, "--no-routing");
        bool noRecycleBin = Has(args, "--no-recycle-bin");
        Silent = silent;

        // Crisp text at 125% / 150%: tell Windows we handle scaling ourselves
        // before any window or font is created.
        try { SetProcessDPIAware(); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            if (silent)
            {
                if (uninstall) Installer.Uninstall(null);
                else Installer.Install(!noRouting, !noRecycleBin, null);
                return;
            }
            Application.Run(new SetupForm(uninstall));
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool Has(string[] args, string flag)
    {
        foreach (string a in args)
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

// ---------------------------------------------------------------------------
//  Palette and small drawing helpers shared by the whole window
// ---------------------------------------------------------------------------

internal static class Theme
{
    internal static readonly Color Bg        = Color.FromArgb(0x1E, 0x20, 0x23);
    internal static readonly Color BgTop     = Color.FromArgb(0x24, 0x27, 0x2B);
    internal static readonly Color Card      = Color.FromArgb(0x2A, 0x2D, 0x32);
    internal static readonly Color CardHover = Color.FromArgb(0x31, 0x35, 0x3A);
    internal static readonly Color Line      = Color.FromArgb(0x3A, 0x3E, 0x44);
    internal static readonly Color Ink       = Color.FromArgb(0xEC, 0xEF, 0xF2);
    internal static readonly Color InkDim    = Color.FromArgb(0x9A, 0xA0, 0xA8);
    internal static readonly Color InkFaint  = Color.FromArgb(0x6E, 0x74, 0x7C);
    internal static readonly Color Accent    = Color.FromArgb(0x8C, 0x6E, 0xFA);
    internal static readonly Color AccentHi  = Color.FromArgb(0x9E, 0x85, 0xFB);
    internal static readonly Color Track     = Color.FromArgb(0x35, 0x39, 0x3F);

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static Font Ui(float size, FontStyle style)
    {
        // Microsoft YaHei UI ships with every supported Windows and renders
        // Chinese and Latin well at small sizes.
        try { return new Font("Microsoft YaHei UI", size, style); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style); }
    }
}

// ---------------------------------------------------------------------------
//  Installer logic (unchanged behaviour, verified by the test suite)
// ---------------------------------------------------------------------------

internal static class Installer
{
    internal static string TargetDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FilesCompanion");
        }
    }

    internal static string RecycleBinDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ModernRecycleBin");
        }
    }

    private static string UninstallerPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FilesCompanion-Uninstall.exe");
        }
    }

    internal static string ShimPath { get { return Path.Combine(TargetDir, "FilesOpen.exe"); } }
    internal static string RecycleBinExe { get { return Path.Combine(RecycleBinDir, "RecycleBin.exe"); } }

    private static string RecycleBinMarker { get { return Path.Combine(RecycleBinDir, ".installed-by-files-companion"); } }

    private static readonly string[][] Routes = new string[][]
    {
        new string[] { @"SOFTWARE\Classes\Folder\shell\open\command",             "%1", "1" },
        new string[] { @"SOFTWARE\Classes\Folder\shell\explore\command",          "%1", "1" },
        new string[] { @"SOFTWARE\Classes\Folder\shell\OpenWithFiles\command",    "%1", "0" },
        new string[] { @"SOFTWARE\Classes\Directory\shell\OpenWithFiles\command", "%1", "0" },
        new string[] { @"SOFTWARE\Classes\Drive\shell\OpenWithFiles\command",     "%1", "0" },
        new string[] { @"SOFTWARE\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command", "", "1" },
        new string[] { @"SOFTWARE\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell\open\command",          "", "1" },
    };

    private static readonly string[] RecycleBinKeys = new string[]
    {
        @"SOFTWARE\Classes\CLSID\" + Program.RecycleBinClsid + @"\shell\open\command",
        @"SOFTWARE\Classes\CLSID\" + Program.RecycleBinClsid + @"\shell\opennewwindow\command",
    };

    internal static void Install(bool routing, bool withRecycleBin, Action<string> log)
    {
        Say(log, "正在解压文件");
        Directory.CreateDirectory(TargetDir);
        ExtractResource("PAYLOAD_SHIM", TargetDir);

        if (withRecycleBin)
        {
            Say(log, "正在安装回收站");
            ExtractResource("PAYLOAD_RB", RecycleBinDir);
            try { File.WriteAllText(RecycleBinMarker, Program.Version); } catch { }
        }

        try
        {
            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(self, UninstallerPath, StringComparison.OrdinalIgnoreCase))
                File.Copy(self, UninstallerPath, true);
        }
        catch { }
        WriteUninstallCmd();

        if (routing)
        {
            Say(log, "正在接管文件夹与驱动器");
            ApplyRouting(true);
        }

        if (withRecycleBin)
        {
            Say(log, "正在连接回收站");
            ApplyRecycleBin(true, RecycleBinExe);
        }
        else
        {
            ApplyRecycleBin(false, null);
        }

        Say(log, "安装完成");
    }

    internal static void Uninstall(Action<string> log)
    {
        string dir = TargetDir;
        string self = Assembly.GetExecutingAssembly().Location;

        string dirWithSep = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (self.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(UninstallerPath)) File.Copy(self, UninstallerPath, true);
                Process.Start(new ProcessStartInfo(UninstallerPath, "--uninstall") { UseShellExecute = false });
                return;
            }
            catch { }
        }

        Say(log, "正在还原文件夹打开方式");
        ApplyRouting(false);

        Say(log, "正在还原回收站");
        ApplyRecycleBin(false, null);

        Say(log, "正在清理文件");
        DeleteFolder(dir);
        if (File.Exists(RecycleBinMarker)) DeleteFolder(RecycleBinDir);
        DeleteFolder(dir);

        try
        {
            string script = Path.Combine(Path.GetTempPath(), "fc-clean-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                "for /l %%i in (1,1,30) do (\r\n" +
                "  del /f /q \"" + self + "\" >nul 2>&1\r\n" +
                "  rd /s /q \"" + dir + "\" >nul 2>&1\r\n" +
                "  rd /s /q \"" + RecycleBinDir + "\" >nul 2>&1\r\n" +
                "  if not exist \"" + dir + "\" if not exist \"" + self + "\" goto done\r\n" +
                "  ping 127.0.0.1 -n 2 >nul\r\n" +
                ")\r\n" +
                ":done\r\n" +
                "del /f /q \"%~f0\" >nul 2>&1\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }

        Say(log, "卸载完成");
        if (log == null && !Program.Silent)
            MessageBox.Show("已卸载。" + Environment.NewLine +
                "文件夹、驱动器、此电脑的打开方式已恢复为系统默认，回收站也已还原。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void DeleteFolder(string dir)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, true);
                if (!Directory.Exists(dir)) return;
            }
            catch { }
            System.Threading.Thread.Sleep(400);
        }
    }

    private static void ApplyRouting(bool on)
    {
        foreach (string[] r in Routes)
        {
            string sub = r[0];
            string arg = r[1];
            bool delegateIt = r[2] == "1";

            try
            {
                if (on)
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(sub))
                    {
                        if (k == null) continue;
                        string cmd = arg.Length > 0
                            ? "\"" + ShimPath + "\" \"" + arg + "\""
                            : "\"" + ShimPath + "\"";
                        k.SetValue(string.Empty, cmd, RegistryValueKind.ExpandString);
                        if (delegateIt) k.SetValue("DelegateExecute", string.Empty, RegistryValueKind.String);
                    }
                }
                else
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        string current = k.GetValue(string.Empty) as string;
                        if (current == null || current.IndexOf(ShimPath, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                    try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
                }
            }
            catch { }
        }
    }

    private static void ApplyRecycleBin(bool on, string exe)
    {
        foreach (string sub in RecycleBinKeys)
        {
            try
            {
                if (on)
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(sub))
                    {
                        if (k != null) k.SetValue(string.Empty, "\"" + exe + "\"", RegistryValueKind.String);
                    }
                }
                else
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        string current = k.GetValue(string.Empty) as string;
                        if (current == null || current.IndexOf(RecycleBinDir, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                    try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
                }
            }
            catch { }
        }
    }

    private static void ExtractResource(string name, string dir)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        using (Stream s = asm.GetManifestResourceStream(name))
        {
            if (s == null) throw new Exception("安装包损坏：找不到内嵌资源 " + name);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(Path.GetTempPath(), "fc-" + Guid.NewGuid().ToString("N") + ".zip");
            using (FileStream fs = File.Create(zip)) s.CopyTo(fs);
            try
            {
                using (ZipArchive a = ZipFile.OpenRead(zip))
                {
                    foreach (ZipArchiveEntry e in a.Entries)
                    {
                        string dest = Path.Combine(dir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                        if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                }
            }
            finally { try { File.Delete(zip); } catch { } }
        }
    }

    private static void WriteUninstallCmd()
    {
        try
        {
            string body =
                "@echo off\r\n" +
                "chcp 65001 >nul\r\n" +
                "echo Uninstalling " + Program.AppName + " ...\r\n" +
                "\"" + UninstallerPath + "\" --uninstall\r\n" +
                "if errorlevel 1 pause\r\n";
            File.WriteAllText(Path.Combine(TargetDir, "Uninstall.cmd"), body, System.Text.Encoding.Default);
        }
        catch { }
    }

    private static void Say(Action<string> log, string msg)
    {
        if (log != null) log(msg);
    }
}

// ---------------------------------------------------------------------------
//  The window: borderless, DPI aware, drawn entirely with GDI+
// ---------------------------------------------------------------------------

internal sealed class SetupForm : Form
{
    // ---- Win32 for the borderless frame ------------------------------------
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // ---- layout metrics, in logical pixels (scaled by DPI at runtime) ------
    private const int PAD = 30;
    private const int CARD_H = 74;
    private const int ROW_H = 34;

    private readonly bool _uninstall;
    private readonly float _s;                 // DPI scale factor
    private readonly Font _fH1, _fHead, _fBody, _fSmall, _fBtn;

    private bool _optRouting = true;
    private bool _optRecycle = true;
    private string _status;
    private int _progress;                     // 0..100
    private bool _busy;
    private int _hover;                        // 0 none, 1 install, 2 close, 31/32 cards

    internal SetupForm(bool uninstall)
    {
        _uninstall = uninstall;

        using (var g = CreateGraphics()) _s = g.DpiX / 96f;

        // Fonts are created once: building them inside OnPaint leaks GDI handles.
        _fH1    = Theme.Ui(21f, FontStyle.Regular);
        _fHead  = Theme.Ui(12f, FontStyle.Regular);
        _fBody  = Theme.Ui(10.5f, FontStyle.Regular);
        _fSmall = Theme.Ui(9.5f, FontStyle.Regular);
        _fBtn   = Theme.Ui(11f, FontStyle.Regular);

        _status = uninstall ? "点击下方按钮开始卸载。" : "点击下方按钮开始安装。";

        Text = Program.AppName;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        int w = S(640), h = S(uninstall ? 330 : 430);
        ClientSize = new Size(w, h);
        MinimumSize = Size;

        MouseDown += OnAnyMouseDown;
        MouseMove += OnAnyMouseMove;
        MouseClick += OnAnyMouseClick;
        KeyPreview = true;
        KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private int S(int v) { return (int)Math.Round(v * _s); }
    private float SF(float v) { return v * _s; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounded corners; harmless on Windows 10.
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    // ---------------------------------------------------------------- paint

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // background: a very soft vertical lift, no gradients screaming for attention
        using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Theme.BgTop, Theme.Bg, 90f))
            g.FillRectangle(b, 0, 0, Width, Height);

        DrawTitleBar(g);

        int y = S(84);

        // headline
        TextRenderer.DrawText(g, _uninstall ? "卸载 Files Companion" : "安装 Files Companion",
            _fH1, new Point(PAD_S, y), Theme.Ink, TextFormatFlags.NoPadding);
        y += S(38);

        TextRenderer.DrawText(g,
            _uninstall ? "恢复文件夹、驱动器、此电脑的默认打开方式，并删除两个组件。"
                       : "为 Files 补回启动动画与智能路由，并把回收站换成现代化版本。",
            _fBody, new Point(PAD_S, y), Theme.InkDim, TextFormatFlags.NoPadding);
        y += S(26);

        if (!_uninstall)
        {
            DrawOptionCard(g, 0, PAD_S, y, Width - PAD_S * 2, S(CARD_H),
                "Files 增强层",
                "启动动画 · 智能路由 · 此电脑与 Win+E 落到主页",
                _optRouting, _hover == 31);
            y += S(CARD_H) + S(10);

            DrawOptionCard(g, 1, PAD_S, y, Width - PAD_S * 2, S(CARD_H),
                "Modern Recycle Bin",
                "图片预览 · 还原到任意位置 · 复制出来 · 按类型筛选",
                _optRecycle, _hover == 32);
            y += S(CARD_H) + S(24);
        }
        else
        {
            y += S(6);
        }

        // status + progress. The track only appears once work has started: an
        // empty bar at rest reads as a stray divider rather than a progress bar.
        TextRenderer.DrawText(g, _status, _fSmall, new Point(PAD_S, y), Theme.InkFaint, TextFormatFlags.NoPadding);
        y += S(24);

        if (_progress > 0)
        {
            var track = new Rectangle(PAD_S, y, Width - PAD_S * 2, S(6));
            using (var p = Theme.Round(track, track.Height / 2))
            using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);

            int fw = (int)Math.Round(track.Width * (_progress / 100.0));
            if (fw < track.Height) fw = track.Height;
            var fill = new Rectangle(track.X, track.Y, fw, track.Height);
            using (var p = Theme.Round(fill, fill.Height / 2))
            using (var b = new LinearGradientBrush(fill, Theme.AccentHi, Theme.Accent, 0f)) g.FillPath(b, p);
        }

        DrawButtons(g);
        DrawFooter(g);
    }

    private int PAD_S { get { return S(PAD); } }

    private void DrawTitleBar(Graphics g)
    {
        // app mark
        int cx = PAD_S, cy = S(34), r = S(9);
        using (var p = Theme.Round(new Rectangle(cx - r, cy - r, r * 2, r * 2), S(6)))
        using (var b = new LinearGradientBrush(new Rectangle(cx - r, cy - r, r * 2, r * 2), Theme.AccentHi, Theme.Accent, 45f))
            g.FillPath(b, p);

        TextRenderer.DrawText(g, Program.AppName + "  " + Program.Version, _fSmall,
            new Point(cx + S(18), cy - S(8)), Theme.InkFaint, TextFormatFlags.NoPadding);

        // close button
        var box = CloseRect;
        if (_hover == 2)
        {
            using (var p = Theme.Round(box, S(6)))
            using (var b = new SolidBrush(Color.FromArgb(0x3A, 0x2B, 0x2E))) g.FillPath(b, p);
        }
        using (var pen = new Pen(_hover == 2 ? Color.FromArgb(0xFF, 0xB4, 0xB4) : Theme.InkDim, SF(1.4f)))
        {
            int m = S(8);
            g.DrawLine(pen, box.Left + m, box.Top + m, box.Right - m, box.Bottom - m);
            g.DrawLine(pen, box.Right - m, box.Top + m, box.Left + m, box.Bottom - m);
        }
    }

    private Rectangle CloseRect
    {
        get { return new Rectangle(Width - PAD_S - S(12), S(34) - S(12), S(24), S(24)); }
    }

    private void DrawOptionCard(Graphics g, int index, int x, int y, int w, int h, string title, string sub, bool on, bool hover)
    {
        var rect = new Rectangle(x, y, w, h);
        using (var p = Theme.Round(rect, S(12)))
        {
            using (var b = new SolidBrush(hover ? Theme.CardHover : Theme.Card)) g.FillPath(b, p);
            using (var pen = new Pen(on ? Color.FromArgb(0x2E, Theme.Accent) : Theme.Line, SF(1f))) g.DrawPath(pen, p);
        }

        // custom checkbox
        int bs = S(20);
        var boxRect = new Rectangle(x + S(18), y + (h - bs) / 2, bs, bs);
        using (var p = Theme.Round(boxRect, S(6)))
        {
            if (on)
            {
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(0x22, 0x25, 0x29))) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Line, SF(1.2f))) g.DrawPath(pen, p);
            }
        }
        if (on)
        {
            using (var pen = new Pen(Color.White, SF(2f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                int bx = boxRect.X, by = boxRect.Y, s2 = bs;
                g.DrawLine(pen, bx + s2 * 0.26f, by + s2 * 0.52f, bx + s2 * 0.44f, by + s2 * 0.70f);
                g.DrawLine(pen, bx + s2 * 0.44f, by + s2 * 0.70f, bx + s2 * 0.76f, by + s2 * 0.30f);
            }
        }

        int tx = boxRect.Right + S(16);
        TextRenderer.DrawText(g, title, _fHead, new Point(tx, y + S(14)), on ? Theme.Ink : Theme.InkDim, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, sub, _fSmall, new Point(tx, y + S(40)), Theme.InkFaint, TextFormatFlags.NoPadding);
    }

    private Rectangle ButtonRect
    {
        get
        {
            int w = S(124), h = S(40);
            return new Rectangle(Width - PAD_S - w, Height - S(72), w, h);
        }
    }

    private void DrawButtons(Graphics g)
    {
        var r = ButtonRect;
        bool disabled = _busy;
        Color fill = disabled ? Color.FromArgb(0x3A, 0x36, 0x46)
                              : (_hover == 1 ? Theme.AccentHi : Theme.Accent);

        using (var p = Theme.Round(r, S(10)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        }

        string label = _uninstall ? "卸载" : "一键安装";
        var sz = TextRenderer.MeasureText(g, label, _fBtn, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, label, _fBtn,
            new Point(r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2),
            Color.White, TextFormatFlags.NoPadding);
    }

    private void DrawFooter(Graphics g)
    {
        // align with the button row so the bottom bar reads as one line
        var r = ButtonRect;
        string text = "改动写入当前用户注册表，可随时卸载还原";
        var sz = TextRenderer.MeasureText(g, text, _fSmall, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, text, _fSmall,
            new Point(PAD_S, r.Y + (r.Height - sz.Height) / 2), Theme.InkFaint, TextFormatFlags.NoPadding);
    }

    // ---------------------------------------------------------------- input

    private void OnAnyMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (CloseRect.Contains(e.Location)) return;
        if (ButtonRect.Contains(e.Location)) return;
        // drag the borderless window
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OnAnyMouseMove(object sender, MouseEventArgs e)
    {
        int h = 0;
        if (CloseRect.Contains(e.Location)) h = 2;
        else if (ButtonRect.Contains(e.Location)) h = 1;
        else if (!_uninstall && !_busy)
        {
            int y = S(84) + S(38) + S(26);
            var c1 = new Rectangle(PAD_S, y, Width - PAD_S * 2, S(CARD_H));
            var c2 = new Rectangle(PAD_S, c1.Bottom + S(10), Width - PAD_S * 2, S(CARD_H));
            if (c1.Contains(e.Location)) h = 31;
            else if (c2.Contains(e.Location)) h = 32;
        }
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    private void OnAnyMouseClick(object sender, MouseEventArgs e)
    {
        if (_busy) return;

        if (CloseRect.Contains(e.Location)) { Close(); return; }
        if (ButtonRect.Contains(e.Location)) { Start(); return; }

        if (!_uninstall)
        {
            int y = S(84) + S(38) + S(26);
            var c1 = new Rectangle(PAD_S, y, Width - PAD_S * 2, S(CARD_H));
            var c2 = new Rectangle(PAD_S, c1.Bottom + S(10), Width - PAD_S * 2, S(CARD_H));
            if (c1.Contains(e.Location)) { _optRouting = !_optRouting; Invalidate(); return; }
            if (c2.Contains(e.Location)) { _optRecycle = !_optRecycle; Invalidate(); return; }
        }
    }

    private void Start()
    {
        _busy = true;
        _progress = 8;
        Invalidate();
        Application.DoEvents();

        try
        {
            if (_uninstall)
            {
                Installer.Uninstall(delegate(string m) { _status = m + "…"; _progress = Math.Min(92, _progress + 18); Pump(); });
                _progress = 100; Pump();
                Close();
            }
            else
            {
                Installer.Install(_optRouting, _optRecycle,
                    delegate(string m) { _status = m + "…"; _progress = Math.Min(92, _progress + 18); Pump(); });
                _progress = 100;
                _status = "安装完成";
                Pump();

                MessageBox.Show(this,
                    "安装完成。现在双击任意文件夹或按 Win+E 试试 Files。",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
        }
        catch (Exception ex)
        {
            _busy = false;
            _progress = 0;
            _status = "出错了：" + ex.Message;
            Invalidate();
            MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Pump() { Invalidate(); Update(); Application.DoEvents(); }
}
