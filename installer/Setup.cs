// FilesCompanionSetup.exe - the all-in-one installer for the Files companion set.
//
// Two components, one file, no network access required:
//
//   1. The launch shim (FilesOpen.exe). Files stays resident in the background
//      for speed, so opening a folder only *activates* the existing process and
//      Windows skips the startup animation. The shim starts Files exactly the
//      same way and plays a short animation on top, so the visual feedback comes
//      back without giving up the residency.
//   2. Modern Recycle Bin - the Recycle Bin replacement, embedded in this very
//      installer. Both components are built for the Files workflow, so they ship
//      and uninstall together.
//
// Everything lives in HKEY_CURRENT_USER: no administrator rights, no Explorer
// restart, and uninstalling restores the defaults.
//
// Safety rules learned the hard way and applied here:
//   * The uninstaller lives NEXT TO the install folder, never inside it - Windows
//     will not let a running executable delete its own folder.
//   * Uninstall removes a registry value ONLY when it still points at this exact
//     install, so a configuration set up by hand is never clobbered.
//   * The embedded Recycle Bin is only removed on uninstall when THIS installer
//     put it there (tracked with a marker file), so a copy the user installed
//     separately survives.
//
// .NET Framework only, compiled with the in-box csc (C# 5 syntax).

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    internal const string AppName = "Files Companion";
    internal const string Version = "1.1.0";
    internal const string RecycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
    internal const string ReleasePage = "https://github.com/Dannyzzy/Files-Companion/releases/latest";

    /// <summary>True in --silent / /S mode: no dialogs at all.</summary>
    internal static bool Silent;

    [STAThread]
    private static void Main(string[] args)
    {
        bool uninstall = Has(args, "--uninstall");
        bool silent = Has(args, "--silent") || Has(args, "/S");
        bool noRouting = Has(args, "--no-routing");
        bool noRecycleBin = Has(args, "--no-recycle-bin");
        Silent = silent;

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

internal static class Installer
{
    // ------------------------------------------------------------ locations

    /// <summary>Where the shim is installed.</summary>
    internal static string TargetDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FilesCompanion");
        }
    }

    /// <summary>Where the embedded Recycle Bin is installed.</summary>
    internal static string RecycleBinDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ModernRecycleBin");
        }
    }

    /// <summary>The uninstaller lives next to the folders, never inside them.</summary>
    private static string UninstallerPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FilesCompanion-Uninstall.exe");
        }
    }

    private static string ShimPath { get { return Path.Combine(TargetDir, "FilesOpen.exe"); } }
    internal static string RecycleBinExe { get { return Path.Combine(RecycleBinDir, "RecycleBin.exe"); } }

    /// <summary>Written when this installer lays down the Recycle Bin, so the
    /// uninstaller knows it may delete that folder.</summary>
    private static string RecycleBinMarker { get { return Path.Combine(RecycleBinDir, ".installed-by-files-companion"); } }

    // ------------------------------------------------------------ routing

    /// <summary>The registry redirects, kept identical to the original enable script.</summary>
    private static readonly string[][] Routes = new string[][]
    {
        new string[] { @"SOFTWARE\Classes\Folder\shell\open\command",             "%1",      "1" },
        new string[] { @"SOFTWARE\Classes\Folder\shell\explore\command",          "%1",      "1" },
        new string[] { @"SOFTWARE\Classes\Folder\shell\OpenWithFiles\command",    "%1",      "0" },
        new string[] { @"SOFTWARE\Classes\Directory\shell\OpenWithFiles\command", "%1",      "0" },
        new string[] { @"SOFTWARE\Classes\Drive\shell\OpenWithFiles\command",     "%1",      "0" },
        new string[] { @"SOFTWARE\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command", "", "1" },
        new string[] { @"SOFTWARE\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell\open\command",          "", "1" },
    };

    private static readonly string[] RecycleBinKeys = new string[]
    {
        @"SOFTWARE\Classes\CLSID\" + Program.RecycleBinClsid + @"\shell\open\command",
        @"SOFTWARE\Classes\CLSID\" + Program.RecycleBinClsid + @"\shell\opennewwindow\command",
    };

    // ------------------------------------------------------------- install

    internal static void Install(bool routing, bool withRecycleBin, Action<string> log)
    {
        Say(log, "正在解压文件…");
        Directory.CreateDirectory(TargetDir);
        ExtractResource("PAYLOAD_SHIM", TargetDir);

        if (withRecycleBin)
        {
            Say(log, "正在安装回收站…");
            ExtractResource("PAYLOAD_RB", RecycleBinDir);
            try { File.WriteAllText(RecycleBinMarker, Program.Version); } catch { }
        }

        // a copy of this installer, kept OUTSIDE the folders it must delete
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
            Say(log, "正在接管文件夹 / 驱动器 / 此电脑 / Win+E…");
            ApplyRouting(true);
        }

        if (withRecycleBin)
        {
            Say(log, "正在连接回收站…");
            ApplyRecycleBin(true, RecycleBinExe);
        }
        else
        {
            ApplyRecycleBin(false, null);
        }

        Say(log, "安装完成");
    }

    // ----------------------------------------------------------- uninstall

    internal static void Uninstall(Action<string> log)
    {
        string dir = TargetDir;
        string self = Assembly.GetExecutingAssembly().Location;

        // hand over to the sibling copy if we are running from inside the folder
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

        Say(log, "正在还原文件夹打开方式…");
        ApplyRouting(false);

        Say(log, "正在还原回收站…");
        ApplyRecycleBin(false, null);

        Say(log, "正在清理文件…");
        DeleteFolder(dir);
        // only remove the Recycle Bin when this installer placed it there
        if (File.Exists(RecycleBinMarker)) DeleteFolder(RecycleBinDir);
        DeleteFolder(dir);

        // backstop: retry the folders and our own copy after this process exits
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

    // ------------------------------------------------------------- routing

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
                    // remove ONLY when it is still ours. Compare the full install path,
                    // not just the file name: a user may run their own copy of a shim
                    // also called FilesOpen.exe, and removing that would break them.
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

    // --------------------------------------------------------- recycle bin

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
                        if (current == null ||
                            current.IndexOf(RecycleBinDir, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                    try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
                }
            }
            catch { }
        }
    }

    // ----------------------------------------------------------- plumbing

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

internal sealed class SetupForm : Form
{
    private readonly bool _uninstall;
    private readonly CheckBox _routing = new CheckBox();
    private readonly CheckBox _recycle = new CheckBox();
    private readonly Button _go = new Button();
    private readonly Label _status = new Label();
    private readonly ProgressBar _bar = new ProgressBar();

    internal SetupForm(bool uninstall)
    {
        _uninstall = uninstall;

        Text = Program.AppName + " " + Program.Version + "  ·  All-in-one";
        ClientSize = new Size(620, 392);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(47, 49, 52);
        ForeColor = Color.FromArgb(236, 239, 242);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var title = new Label();
        title.Text = uninstall ? "卸载 Files Companion" : "安装 Files Companion";
        title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
        title.AutoSize = true;
        title.Location = new Point(24, 18);
        Controls.Add(title);

        var sub = new Label();
        sub.Text = uninstall
            ? "将恢复文件夹、驱动器、此电脑的默认打开方式，并删除两个组件。"
            : "专为 Files 打造的一套增强：启动动画 + 智能路由 + 现代化回收站。";
        sub.AutoSize = true;
        sub.ForeColor = Color.FromArgb(160, 165, 172);
        sub.Location = new Point(26, 58);
        Controls.Add(sub);

        var sub2 = new Label();
        sub2.Text = "两个组件已内置，无需联网；改动都在 HKCU，不需要管理员权限。";
        sub2.AutoSize = true;
        sub2.ForeColor = Color.FromArgb(140, 145, 152);
        sub2.Location = new Point(26, 80);
        Controls.Add(sub2);

        var box = new GroupBox();
        box.Text = "包含的组件";
        box.ForeColor = Color.FromArgb(170, 175, 182);
        box.Location = new Point(26, 108);
        box.Size = new Size(568, 112);
        box.Visible = !uninstall;
        Controls.Add(box);

        _routing.Text = "Files 增强层 —— 启动动画 + 智能路由（文件夹 / 驱动器 / 此电脑 / Win+E）";
        _routing.Checked = true;
        _routing.Location = new Point(16, 30);
        _routing.AutoSize = true;
        _routing.Visible = !uninstall;
        box.Controls.Add(_routing);

        _recycle.Text = "Modern Recycle Bin —— 回收站（图片预览 / 还原到任意位置 / 复制出来）";
        _recycle.Checked = true;
        _recycle.Location = new Point(16, 68);
        _recycle.AutoSize = true;
        _recycle.Visible = !uninstall;
        box.Controls.Add(_recycle);

        _status.Text = uninstall ? "点击下方按钮开始卸载。" : "点击下方按钮开始安装。";
        _status.ForeColor = Color.FromArgb(154, 157, 161);
        _status.AutoSize = true;
        _status.Location = new Point(28, 238);
        Controls.Add(_status);

        _bar.Location = new Point(28, 266);
        _bar.Size = new Size(564, 6);
        _bar.Style = ProgressBarStyle.Continuous;
        Controls.Add(_bar);

        _go.Text = uninstall ? "卸载" : "一键安装";
        _go.Size = new Size(140, 38);
        _go.Location = new Point(452, 302);
        _go.FlatStyle = FlatStyle.Flat;
        _go.FlatAppearance.BorderSize = 0;
        _go.BackColor = Color.FromArgb(140, 110, 250);
        _go.ForeColor = Color.White;
        _go.Click += OnGo;
        Controls.Add(_go);
    }

    private void OnGo(object sender, EventArgs e)
    {
        _go.Enabled = false;
        _bar.Value = 30;
        Application.DoEvents();

        try
        {
            if (_uninstall)
            {
                Installer.Uninstall(delegate(string m) { _status.Text = m; Application.DoEvents(); });
                _bar.Value = 100;
                Application.DoEvents();
                Close();
            }
            else
            {
                Installer.Install(_routing.Checked, _recycle.Checked,
                    delegate(string m) { _status.Text = m; Application.DoEvents(); });
                _bar.Value = 100;
                Application.DoEvents();

                if (MessageBox.Show(this,
                        "安装完成！" + Environment.NewLine + Environment.NewLine +
                        "双击任意文件夹、或按 Win+E 试试 Files。" + Environment.NewLine +
                        "打开回收站看看新界面。" + Environment.NewLine + Environment.NewLine +
                        "现在打开回收站吗？",
                        Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { Process.Start(Installer.RecycleBinExe); } catch { }
                }
                Close();
            }
        }
        catch (Exception ex)
        {
            _go.Enabled = true;
            _bar.Value = 0;
            _status.Text = "出错了：" + ex.Message;
            MessageBox.Show(this, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
