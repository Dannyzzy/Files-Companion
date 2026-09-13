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

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
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

        // Self-check: writes a report of everything this machine offers and opens
        // it in Notepad. Exists so a user (or we) can tell in seconds whether the
        // companion will work here, instead of guessing from a silent no-op.
        if (target == "--doctor")
        {
            RunDoctor();
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

    /// <summary>
    /// Starts Files, then plays the animation.
    ///
    /// Files can be installed in several ways and each one puts its launcher
    /// somewhere different: the Store build uses an App Execution Alias, the
    /// classic GitHub installer drops a launcher under %LOCALAPPDATA%\Files,
    /// winget and scoop use their own trees. Probing a single hardcoded path is
    /// why a folder open could do nothing at all on somebody else's machine, so
    /// every plausible location is tried and, failing everything, Explorer takes
    /// over - a working window beats a silent no-op.
    /// </summary>
    private static void LaunchFiles(string target)
    {
        // A protocol URI carries an app-internal target (for example the Home
        // page). It must go through ShellExecute: the launcher below would treat
        // it as a file-system path and fail.
        if (!string.IsNullOrEmpty(target) &&
            (target.StartsWith("files-stable:", StringComparison.OrdinalIgnoreCase) ||
             target.StartsWith("files-preview:", StringComparison.OrdinalIgnoreCase) ||
             target.StartsWith("files:", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }
            catch { /* fall through to the normal path */ }
        }

        string how;
        string exe = FindFilesLauncher(out how);

        if (string.IsNullOrEmpty(exe))
        {
            Log("no Files launcher found -> falling back to Explorer");
            FallbackToExplorer(target);
            return;
        }

        // "This PC", its namespace siblings and Win+E: activating Files with NO
        // argument reuses the running instance AND lands on the Home page - the one
        // with the drive capacity cards. (Measured.) Passing the namespace CLSID
        // would open the bare item list, and a protocol URI would spawn a whole new
        // process - both worse.
        string argument = string.IsNullOrEmpty(target) || IsThisPc(target)
            ? string.Empty
            : Quote(target);

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = argument
            });
            Log("launched via " + how + ": " + exe);
        }
        catch (Exception ex)
        {
            Log("launch failed (" + ex.ToString() + ") -> falling back to Explorer");
            FallbackToExplorer(target);
        }
    }

    /// <summary>
    /// Locates Files' launcher, trying every install flavour in turn. The "how"
    /// out-parameter names the strategy that matched, which is what makes the
    /// self-check report readable.
    /// </summary>
    private static string FindFilesLauncher(out string how)
    {
        how = null;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // 1) classic installer: a launcher directly under %LOCALAPPDATA%\Files
        try
        {
            string a = Path.Combine(local, @"Files\Files.App.Launcher.exe");
            if (File.Exists(a)) { how = "%LOCALAPPDATA%\\Files"; return a; }
        }
        catch { }

        // 2) Store build: look at the execution aliases that actually exist rather
        //    than hardcoding "files-stable" - preview and dev builds differ, and
        //    the alias name is a package manifest detail that can change.
        try
        {
            string apps = Path.Combine(local, @"Microsoft\WindowsApps");
            if (Directory.Exists(apps))
            {
                string[] hits = Directory.GetFiles(apps, "files*.exe");
                if (hits.Length > 0)
                {
                    Array.Sort(hits, delegate(string x, string y)
                    {
                        int sx = x.IndexOf("stable", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
                        int sy = y.IndexOf("stable", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
                        if (sx != sy) return sx - sy;
                        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                    });
                    how = "Store alias (" + Path.GetFileName(hits[0]) + ", " + hits.Length + " found)";
                    return hits[0];
                }
            }
        }
        catch { }

        // 3) a machine-wide install
        foreach (string root in new string[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                string b = Path.Combine(root, @"Files\Files.App.Launcher.exe");
                if (File.Exists(b)) { how = "Program Files"; return b; }
            }
            catch { }
        }

        // 4) the package root recorded by the AppX repository - covers a Store
        //    install whose alias folder is missing or locked down
        try
        {
            string repo = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(repo))
            {
                if (k != null)
                {
                    string[] subs = k.GetSubKeyNames();
                    Array.Sort(subs);
                    Array.Reverse(subs);   // newest package version first
                    foreach (string sub in subs)
                    {
                        if (sub.IndexOf("Files_", StringComparison.OrdinalIgnoreCase) != 0) continue;
                        using (Microsoft.Win32.RegistryKey pk = k.OpenSubKey(sub))
                        {
                            if (pk == null) continue;
                            string proot = pk.GetValue("PackageRoot") as string;
                            if (string.IsNullOrEmpty(proot)) continue;
                            string c = Path.Combine(proot, "Files.App.Launcher.exe");
                            if (File.Exists(c)) { how = "AppX package (" + sub.Split('_')[0] + ")"; return c; }
                        }
                    }
                }
            }
        }
        catch { }

        // 5) winget and scoop trees
        try
        {
            string winget = Path.Combine(local, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(winget))
            {
                foreach (string d in Directory.GetDirectories(winget, "Files*"))
                {
                    string c = Path.Combine(d, "Files.App.Launcher.exe");
                    if (File.Exists(c)) { how = "winget"; return c; }
                }
            }
        }
        catch { }
        try
        {
            string scoop = Path.Combine(roaming, @"scoop\apps\files\current\Files.App.Launcher.exe");
            if (File.Exists(scoop)) { how = "scoop"; return scoop; }
        }
        catch { }

        // 6) last resort before Explorer: ask the shell to resolve the alias by name
        try
        {
            string alias = Path.Combine(local, @"Microsoft\WindowsApps\files-stable.exe");
            if (File.Exists(alias)) { how = "Store alias (files-stable)"; return alias; }
        }
        catch { }

        return null;
    }

    /// <summary>Opens the folder the way Windows would have if this shim were not
    /// installed, so an unrecognised Files install never leaves a dead double-click.</summary>
    private static void FallbackToExplorer(string target)
    {
        try
        {
            if (string.IsNullOrEmpty(target) || IsThisPc(target))
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = Quote(target) });
        }
        catch { }
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

    /// <summary>
    /// Writes a report of everything the companion depends on and opens it in
    /// Notepad.  Usage:  FilesOpen.exe --doctor
    ///
    /// This exists because the interesting failures are silent: if Files cannot be
    /// found, a folder open simply does nothing. Rather than have someone guess,
    /// the report says exactly what was detected on THIS machine.
    /// </summary>
    private static void RunDoctor()
    {
        string text = "";
        text += "Files Companion - self check" + Environment.NewLine;
        text += "=============================" + Environment.NewLine;
        text += "Time        : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine;
        text += "Windows     : " + Environment.OSVersion.VersionString + Environment.NewLine;
        text += "64-bit OS   : " + (Environment.Is64BitOperatingSystem ? "yes" : "no") + Environment.NewLine;
        text += "64-bit proc : " + (Environment.Is64BitProcess ? "yes" : "no") + Environment.NewLine;
        // This process deliberately stays DPI-unaware (see PlayAnimation), which
        // makes the desktop DC report a virtualised 96 dpi on a scaled display.
        // --doctor runs before any window exists and exits straight after, so
        // becoming aware here is safe - and it is the only way to print the real
        // number instead of a number that hides the very scaling the user sees.
        int dpi = 0;
        try { SetProcessDPIAware(); } catch { }
        try { dpi = (int)GetDpiForSystem(); } catch { dpi = 0; }
        if (dpi <= 0)
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = (int)g.DpiX; } catch { }
        }
        if (dpi <= 0) dpi = 96;
        text += "Display DPI : " + dpi + " (" + (int)Math.Round(dpi / 96.0 * 100) + "%)" + Environment.NewLine;
        text += Environment.NewLine;

        // --- where is Files ---------------------------------------------------
        text += "Files launcher" + Environment.NewLine;
        string how;
        string exe = FindFilesLauncher(out how);
        if (!string.IsNullOrEmpty(exe))
        {
            text += "  OK      : " + exe + Environment.NewLine;
            text += "  found by: " + how + Environment.NewLine;
        }
        else
        {
            text += "  MISSING : could not locate a Files launcher." + Environment.NewLine;
            text += "            Folder opens will fall back to Explorer." + Environment.NewLine;
            text += "            Install Files from https://github.com/files-community/Files" + Environment.NewLine;
            text += "            (or the Microsoft Store) and run this check again." + Environment.NewLine;
        }
        text += Environment.NewLine;

        // --- the shell redirects ---------------------------------------------
        text += "Shell redirects (HKEY_CURRENT_USER)" + Environment.NewLine;
        string[][] routes = new string[][]
        {
            new string[] { "Folders  ", @"SOFTWARE\Classes\Folder\shell\open\command" },
            new string[] { "Explore  ", @"SOFTWARE\Classes\Folder\shell\explore\command" },
            new string[] { "Namespace", @"SOFTWARE\Classes\Folder\shell\OpenWithFiles\command" },
            new string[] { "Directory", @"SOFTWARE\Classes\Directory\shell\OpenWithFiles\command" },
            new string[] { "Drives   ", @"SOFTWARE\Classes\Drive\shell\OpenWithFiles\command" },
            new string[] { "Win+E    ", @"SOFTWARE\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command" },
            new string[] { "This PC  ", @"SOFTWARE\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell\open\command" },
            new string[] { "Recycle  ", @"SOFTWARE\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open\command" },
        };
        bool anyRoute = false;
        foreach (string[] r in routes)
        {
            string value = null;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(r[1]))
                    if (k != null) value = k.GetValue(string.Empty) as string;
            }
            catch { }
            text += "  " + r[0] + " : " + (string.IsNullOrEmpty(value) ? "(not set)" : value) + Environment.NewLine;
            if (!string.IsNullOrEmpty(value)) anyRoute = true;
        }
        if (!anyRoute)
            text += "  NOTE: nothing is redirected, so the enhancement layer is not installed." + Environment.NewLine;
        text += Environment.NewLine;

        // --- the Recycle Bin component ---------------------------------------
        // The bin does not have to live in the folder we would have installed it
        // to: a hand-made setup points the shell keys at its own copy, and calling
        // that "missing" is the very mistake the shim check used to make.
        text += "Recycle Bin component" + Environment.NewLine;
        string rb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"ModernRecycleBin\RecycleBin.exe");
        string customRb = null;
        if (!File.Exists(rb))
        {
            customRb = ExistingExe(RedirectTarget(
                @"SOFTWARE\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open\command"));
        }
        if (File.Exists(rb))
            text += "  OK      : " + rb + Environment.NewLine;
        else if (customRb != null)
            text += "  OK      : " + customRb + " (custom location)" + Environment.NewLine;
        else
            text += "  MISSING : " + rb + Environment.NewLine;
        text += "  WebView2: " + (WebView2Present() ? "installed" : "MISSING - the Recycle Bin UI needs it") + Environment.NewLine;
        text += Environment.NewLine;

        // --- verdict ----------------------------------------------------------
        bool ok = !string.IsNullOrEmpty(exe) && anyRoute;
        text += "Result" + Environment.NewLine;
        if (ok)
            text += "  Ready. Double-clicking a folder should open Files with the launch animation." + Environment.NewLine;
        else if (string.IsNullOrEmpty(exe))
            text += "  Not ready: Files was not found. Folder opens will use Explorer instead." + Environment.NewLine;
        else
            text += "  Not ready: the shell redirects are missing. Run the installer again." + Environment.NewLine;

        string outPath = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "doctor.txt");
        try { File.WriteAllText(outPath, text); } catch { }
        Log("doctor written to " + outPath);

        try { Process.Start("notepad.exe", "\"" + outPath + "\""); } catch { }
    }

    /// <summary>Reads the command line a shell key is redirected to. Null when the
    /// key is absent or unreadable.</summary>
    private static string RedirectTarget(string sub)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sub))
                if (k != null) return k.GetValue(string.Empty) as string;
        }
        catch { }
        return null;
    }

    /// <summary>Pulls the executable out of a shell command line
    /// ("\"C:\\dir\\x.exe\" \"%1\"" becomes C:\dir\x.exe) and returns it only when
    /// the file is still there - a stale redirect must not read as healthy.</summary>
    private static string ExistingExe(string command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        string p = command.Trim();
        if (p.StartsWith("\""))
        {
            int end = p.IndexOf('"', 1);
            if (end < 1) return null;
            p = p.Substring(1, end - 1);
        }
        else
        {
            int sp = p.IndexOf(' ');
            if (sp > 0) p = p.Substring(0, sp);
        }
        p = p.Trim();
        return (p.Length > 0 && File.Exists(p)) ? p : null;
    }

    /// <summary>The Recycle Bin UI is rendered by WebView2, so its runtime has to be
    /// present. Windows 11 and current Windows 10 ship it.</summary>
    private static bool WebView2Present()
    {
        string[] keys = new string[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        };
        foreach (string sub in keys)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub))
                {
                    if (k != null)
                    {
                        string pv = k.GetValue("pv") as string;
                        if (!string.IsNullOrEmpty(pv)) return true;
                    }
                }
            }
            catch { }
        }
        return false;
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
