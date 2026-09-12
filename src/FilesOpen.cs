// FilesOpen.exe - a lightweight launch-animation layer for the Files app.
//
// Why this exists:
//   The Windows "startup animation" is played by the OS when a process is
//   launched. Files is kept resident in the background (LeaveAppRunning) for
//   speed, so opening a folder only *activates* an existing process and the
//   animation is skipped. This shim restores the animation without giving up
//   the residency: it starts Files exactly like before, then plays a short
//   icon fade/zoom on top.
//
// Behaviour:
//   FilesOpen.exe "<path>"   ->  start Files with <path>, play the animation
//   FilesOpen.exe            ->  start Files with no argument (Win+E), animate
//
// Safety:
//   * Files is started FIRST, before any UI work, so the animation can never
//     delay or block the actual folder opening.
//   * Every failure path still exits quickly; the process never lingers.
//   * A named mutex prevents overlapping animations when several folders are
//     opened in quick succession.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private const string HomeUri = "files-stable:?folder=Home";
    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int WS_EX_LAYERED = 0x00080000;

    private static readonly string IconPath =
        Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "files-icon.png");

    private static Bitmap _icon;
    private static int _w, _h;

    private sealed class LayeredForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        string target = args.Length > 0 ? args[0] : null;
        Log("argv=[" + string.Join("|", args) + "]");

        // Pre-warm mode, used by the logon shortcut. Files paints its window black
        // for roughly half a second while a COLD start loads its content, so the
        // first open after a logon flashes black. Starting it once in the
        // background removes that cold start entirely - and opening is faster too.
        if (target == "--warmup")
        {
            WarmUp();
            return;
        }

        // The Recycle Bin is served by our own viewer (RecycleBin.exe, next to this
        // file): a plain WinForms window, so it neither flashes the screen black
        // the way a Files window does, nor leaves a File Explorer button in the
        // taskbar the way Explorer's own Recycle Bin does.
        if (!string.IsNullOrEmpty(target) && IsRecycleBin(target))
        {
            Log("recycle bin -> RecycleBin.exe");
            LaunchRecycleBinViewer();
            return;
        }

        // 1) Open the folder first - never let the animation delay this.
        LaunchFiles(target);

        // 2) Play the animation if an icon is available and none is running.
        if (!File.Exists(IconPath)) return;

        bool owned;
        using (var mutex = new Mutex(true, @"Local\FilesOpenAnimation", out owned))
        {
            if (!owned) return;
            try { PlayAnimation(); }
            catch { /* never surface an error for a cosmetic effect */ }
        }
    }

    /// <summary>
    /// Starts Files and immediately closes its window again. LeaveAppRunning keeps
    /// the process alive, so every later open is a warm activation: no cold start,
    /// no black frame, no wait.
    /// </summary>
    private static void WarmUp()
    {
        try
        {
            LaunchFiles(null);

            for (int i = 0; i < 150; i++)
            {
                System.Threading.Thread.Sleep(200);

                bool closed = false;
                foreach (Process p in Process.GetProcessesByName("Files"))
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            p.CloseMainWindow();
                            closed = true;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                if (closed) break;
            }
        }
        catch { }
    }

    private static void LaunchFiles(string target)
    {
        try
        {
            // A "files-stable:" URI carries an app-internal target (for example the
            // Home page). It must go through ShellExecute - the launcher below would
            // treat it as a file-system path and fail.
            if (!string.IsNullOrEmpty(target) &&
                target.StartsWith("files-stable:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }

            // "This PC", its namespace siblings and Win+E: activating Files with NO
            // argument reuses the running instance AND lands on the Home page - the
            // one with the drive capacity cards. (Measured.) Passing the namespace
            // CLSID would open the bare item list instead, and the protocol URI
            // would spawn a whole new process - both worse.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string launcher = Path.Combine(home, "Files", "Files.App.Launcher.exe");
            string exe = File.Exists(launcher)
                ? launcher
                : Path.Combine(home, @"Microsoft\WindowsApps\files-stable.exe");

            // Folder opens reuse the running instance too: one process, one taskbar
            // entry, ~465 MB in total, and the window paints in ~0.3 s.
            string argument = string.IsNullOrEmpty(target) || IsThisPc(target)
                ? string.Empty
                : Quote(target);

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = argument
            });
        }
        catch
        {
            // Nothing useful to do here; fall through and exit quietly.
        }
    }

    private static string Quote(string s)
    {
        return "\"" + s.TrimEnd('\\') + "\"";
    }

    /// <summary>Small rolling log, so shell invocations can be diagnosed later.</summary>
    private static void Log(string line)
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "filesopen.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 64 * 1024) info.Delete();
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    private static List<IntPtr> CabinetWindows()
    {
        var found = new List<IntPtr>();
        var cls = new StringBuilder(64);

        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (!IsWindowVisible(h)) return true;
            cls.Length = 0;
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "CabinetWClass") found.Add(h);
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Opens the Recycle Bin with Explorer, then takes the resulting window off the
    /// taskbar (WS_EX_TOOLWINDOW) so no "File Explorer" button shows up next to the
    /// Files icon. Explorer is used here because showing a Files window flashes the
    /// screen black for ~0.5 s on this machine.
    /// </summary>
    private static void OpenRecycleBinInExplorer()
    {
        List<IntPtr> before = CabinetWindows();

        Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
        {
            UseShellExecute = true
        });

        for (int attempt = 0; attempt < 80; attempt++)
        {
            System.Threading.Thread.Sleep(100);

            foreach (IntPtr h in CabinetWindows())
            {
                if (before.Contains(h)) continue;

                long style = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                style |= WS_EX_TOOLWINDOW;
                style &= ~WS_EX_APPWINDOW;
                SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(style));

                // the shell only re-evaluates the taskbar button on a visibility change
                ShowWindow(h, SW_HIDE);
                ShowWindow(h, SW_SHOWNA);
                return;
            }
        }
    }

    /// <summary>Runs our own Recycle Bin viewer; falls back to Explorer's if the
    /// viewer is missing for any reason.</summary>
    private static void LaunchRecycleBinViewer()
    {
        try
        {
            string exe = Path.Combine(
                Path.GetDirectoryName(typeof(Program).Assembly.Location), "RecycleBin.exe");

            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
                return;
            }
        }
        catch { }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>True when the shell handed us the Recycle Bin namespace root.</summary>
    private static bool IsRecycleBin(string target)
    {
        if (string.IsNullOrEmpty(target)) return false;

        return target.IndexOf("645FF040-5081-101B-9F08-00AA002F954E", StringComparison.OrdinalIgnoreCase) >= 0
            || target.IndexOf("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>True when the shell handed us the "This PC" namespace root.</summary>
    private static bool IsThisPc(string target)
    {
        if (string.IsNullOrEmpty(target)) return false;

        return target.IndexOf("20D04FE0-3AEA-1069-A2D8-08002B30309D", StringComparison.OrdinalIgnoreCase) >= 0
            || target.IndexOf("shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void PlayAnimation()
    {
        // NOTE: deliberately NO SetProcessDPIAware() here. Changing DPI awareness
        // after the process has already started makes Windows re-run its display
        // scaling, which shows up as a full-screen black flash. The DPI is read
        // from the desktop DC instead, which needs no awareness change.
        _icon = new Bitmap(IconPath);

        float dpi;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f;

        _w = _h = (int)Math.Round(120 * dpi);

        Screen screen = Screen.FromPoint(Cursor.Position);
        Rectangle wa = screen.WorkingArea;
        int x = wa.Left + (wa.Width - _w) / 2;
        int y = wa.Top + (wa.Height - _h) / 2;

        using (var form = new LayeredForm())
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = new Rectangle(x, y, _w, _h);
            form.TopMost = true;

            // Paint a fully transparent surface BEFORE the window is shown: a
            // layered window that becomes visible before its first
            // UpdateLayeredWindow displays an uninitialised - i.e. black - surface
            // for one frame.
            IntPtr hwnd = form.Handle;
            Render(hwnd, x, y, 0f, 0.92f);

            form.Show();
            Application.DoEvents();

            // One smooth bloom: fade in while easing up in size, then fade out
            // while easing a little further out. No hold in between - a hold reads
            // as a stall, a continuous movement reads as a flourish.
            Phase(hwnd, x, y, 0.00f, 1.00f, 0.92f, 1.00f, 80);
            Phase(hwnd, x, y, 1.00f, 0.00f, 1.00f, 1.06f, 130);

            form.Hide();
        }

        _icon.Dispose();
        _icon = null;
    }

    private static void Phase(IntPtr hwnd, int x, int y, float a0, float a1, float s0, float s1, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double t = sw.Elapsed.TotalMilliseconds / ms;
            if (t > 1.0) t = 1.0;

            float e = (float)(1.0 - Math.Pow(1.0 - t, 3.0)); // ease-out cubic

            Render(hwnd, x, y, a0 + (a1 - a0) * e, s0 + (s1 - s0) * e);
            Application.DoEvents();

            if (t >= 1.0) break;
            Thread.Sleep(8);
        }
    }

    private static void Render(IntPtr hwnd, int x, int y, float alpha, float scale)
    {
        using (var bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;

                int iw = (int)Math.Round(_w * scale);
                int ih = (int)Math.Round(_h * scale);
                g.DrawImage(_icon, new Rectangle((_w - iw) / 2, (_h - ih) / 2, iw, ih));
            }

            Premultiply(bmp);
            Present(hwnd, x, y, bmp, alpha);
        }
    }

    /// <summary>UpdateLayeredWindow needs premultiplied alpha; GDI+ produces straight alpha.</summary>
    private static void Premultiply(Bitmap bmp)
    {
        Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData d = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(d.Stride) * d.Height;
            byte[] buf = new byte[bytes];
            Marshal.Copy(d.Scan0, buf, 0, bytes);

            for (int i = 0; i + 3 < bytes; i += 4)
            {
                byte a = buf[i + 3];
                if (a == 255) continue;
                if (a == 0)
                {
                    buf[i] = 0; buf[i + 1] = 0; buf[i + 2] = 0;
                    continue;
                }
                buf[i] = (byte)(buf[i] * a / 255);
                buf[i + 1] = (byte)(buf[i + 1] * a / 255);
                buf[i + 2] = (byte)(buf[i + 2] * a / 255);
            }

            Marshal.Copy(buf, 0, d.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(d);
        }
    }

    private static void Present(IntPtr hwnd, int x, int y, Bitmap bmp, float alpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero;
        IntPtr oldBmp = IntPtr.Zero;

        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            oldBmp = SelectObject(memDc, hBmp);

            var dst = new POINT { X = x, Y = y };
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { X = 0, Y = 0 };

            int a = (int)Math.Round(alpha * 255);
            if (a < 0) a = 0;
            if (a > 255) a = 255;

            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)a,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
