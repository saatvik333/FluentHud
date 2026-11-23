using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Fluent HUD Setup")]
[assembly: AssemblyProduct("Fluent HUD")]
[assembly: AssemblyCompany("Saatvik Sharma")]
[assembly: AssemblyCopyright("Copyright © 2026 Saatvik Sharma. MIT License.")]
// AssemblyVersion comes from build.ps1, shared with the overlay.

namespace FluentHudSetup
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        // FluentHudSetup.exe                      interactive install
        // FluentHudSetup.exe /uninstall           interactive uninstall (what Settings > Apps runs)
        // Add /quiet for no UI; a quiet install also takes /startup, /desktop and /launch.
        // A quiet install over an existing one keeps its autostart and desktop shortcut.
        [STAThread]
        static int Main(string[] args)
        {
            bool quiet = Has(args, "/quiet"), uninstall = Has(args, "/uninstall");
            if (quiet)
            {
                try
                {
                    if (uninstall)
                    {
                        Installer.Uninstall();
                        Installer.RemoveInstallFolder();
                    }
                    else
                    {
                        bool update = Installer.IsInstalled;
                        Installer.Install(new Options
                        {
                            Startup = Has(args, "/startup") || (update && Installer.StartsWithWindows),
                            Desktop = Has(args, "/desktop") || (update && Installer.HasDesktopLink),
                            Launch = Has(args, "/launch"),
                        });
                    }
                    return 0;
                }
                catch (Exception e)
                {
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "FluentHudSetup.log"), e.ToString()); } catch { }
                    return 1;
                }
            }

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (uninstall)
            {
                var answer = MessageBox.Show("Remove " + Installer.AppName + " from this PC?\n\nYour saved overlay position and size are kept.",
                    "Uninstall " + Installer.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return 0;
                try
                {
                    Installer.Uninstall();
                    MessageBox.Show(Installer.AppName + " has been removed.", "Uninstall " + Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    // Last, after the message box: this hands off to a helper that needs this process gone.
                    Installer.RemoveInstallFolder();
                    return 0;
                }
                catch (Exception e)
                {
                    MessageBox.Show("Uninstall didn't finish:\n\n" + e.Message, "Uninstall " + Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }

            Application.Run(new SetupForm());
            return 0;
        }

        static bool Has(string[] args, string flag)
        {
            return args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }
    }

    sealed class Options
    {
        public bool Startup, Desktop, Launch;
    }

    // Per-user install: everything lives under the user's profile and HKCU, so no admin prompt.
    static class Installer
    {
        public const string AppName = "Fluent HUD";
        const string Publisher = "Saatvik Sharma";
        const string ExeName = "FluentHud.exe", SetupName = "FluentHudSetup.exe", ProcessName = "FluentHud";
        const string RunValue = "FluentHud";

        // FLUENTHUD_SETUP_TEST_ROOT=<folder>: install into that folder and into test registry keys
        // (HKCU\Software\FluentHudSetupTest) instead of the real ones, so the installer can be
        // exercised end to end without touching an existing install.
        static readonly string TestRoot = Environment.GetEnvironmentVariable("FLUENTHUD_SETUP_TEST_ROOT");
        public const string TestRegistryRoot = @"Software\FluentHudSetupTest";

        static string UninstallKey
        {
            get { return (TestRoot != null ? TestRegistryRoot : @"Software\Microsoft\Windows\CurrentVersion") + @"\Uninstall\FluentHud"; }
        }

        static string RunKey
        {
            get { return (TestRoot != null ? TestRegistryRoot : @"Software\Microsoft\Windows\CurrentVersion") + @"\Run"; }
        }

        public static string Version { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }

        public static string InstallDir
        {
            get { return Path.Combine(TestRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FluentHud"); }
        }

        static string ExePath { get { return Path.Combine(InstallDir, ExeName); } }
        static string SetupPath { get { return Path.Combine(InstallDir, SetupName); } }
        static string Self { get { return Assembly.GetExecutingAssembly().Location; } }

        static string StartMenuLink
        {
            get { return Path.Combine(TestRoot != null ? Path.Combine(TestRoot, "Start Menu") : Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
        }

        // Null when Windows can't resolve the Desktop folder (broken folder redirection).
        static string DesktopLink
        {
            get
            {
                string desktop = TestRoot != null ? Path.Combine(TestRoot, "Desktop") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return desktop.Length == 0 ? null : Path.Combine(desktop, AppName + ".lnk");
            }
        }

        public static bool IsInstalled { get { return File.Exists(ExePath); } }
        public static bool HasDesktopLink { get { return DesktopLink != null && File.Exists(DesktopLink); } }

        public static bool StartsWithWindows
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
        }

        public static void Install(Options options)
        {
            StopRunningCopies();
            Directory.CreateDirectory(InstallDir);
            WritePayload();
            // Keep a copy of this installer next to the app: it is also the uninstaller.
            if (!SamePath(Self, SetupPath)) File.Copy(Self, SetupPath, true);

            CreateShortcut(StartMenuLink);
            if (DesktopLink != null)
            {
                if (options.Desktop) CreateShortcut(DesktopLink); else File.Delete(DesktopLink);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", Publisher);
                key.SetValue("DisplayIcon", ExePath);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", Quote(SetupPath) + " /uninstall");
                key.SetValue("QuietUninstallString", Quote(SetupPath) + " /uninstall /quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)((new FileInfo(ExePath).Length + new FileInfo(SetupPath).Length) / 1024), RegistryValueKind.DWord);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                // --startup tells the overlay Windows started it, so it skips the "running" hint.
                if (options.Startup) key.SetValue(RunValue, Quote(ExePath) + " --startup");
                else key.DeleteValue(RunValue, false);
            }

            if (options.Launch)
                using (Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = InstallDir })) { }
        }

        // Removes everything except the install folder itself; RemoveInstallFolder finishes the job.
        public static void Uninstall()
        {
            StopRunningCopies();
            // The step most likely to fail (a locked file) goes first, while the shortcut and the
            // Settings > Apps entry still exist to try again from.
            if (File.Exists(ExePath)) Retry(() => File.Delete(ExePath));
            File.Delete(StartMenuLink);
            if (DesktopLink != null) File.Delete(DesktopLink);
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (key != null) key.DeleteValue(RunValue, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }

        public static void RemoveInstallFolder()
        {
            if (!Directory.Exists(InstallDir)) return;
            if (!SamePath(Self, SetupPath))
            {
                File.Delete(SetupPath);
                if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
                return;
            }
            // A running program can't delete its own file, so a short-lived shell does it once this
            // process has exited. It removes only this file and then the folder if that left it empty.
            string command = "/c ping -n 3 127.0.0.1 >nul & del /q " + Quote(SetupPath) + " & rmdir " + Quote(InstallDir);
            using (Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            })) { }
        }

        static void StopRunningCopies()
        {
            // A test install must never close the copy someone is actually using.
            if (TestRoot != null) return;
            // Ask politely first: a copy that exits on its own removes its tray icon, a killed one leaves a ghost.
            try
            {
                EventWaitHandle quit;
                if (EventWaitHandle.TryOpenExisting(@"Local\FluentHud.Quit", out quit))
                    using (quit) quit.Set();
            }
            catch (UnauthorizedAccessException) { }   // a copy running as administrator; the loop below still tries

            int session;
            using (Process me = Process.GetCurrentProcess()) session = me.SessionId;
            foreach (Process process in Process.GetProcessesByName(ProcessName))
                using (process)
                {
                    try
                    {
                        if (process.SessionId != session) continue;   // another signed-in user's copy
                        if (process.WaitForExit(4000)) continue;
                        process.Kill();
                        process.WaitForExit(4000);
                    }
                    catch (Exception) { }   // already gone, or not ours to stop
                }
        }

        static void WritePayload()
        {
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("FluentHud.Payload"))
            {
                if (payload == null) throw new InvalidOperationException("This installer was built without the app inside it. Run build.ps1 again.");
                // Write beside the target and swap it in, so a failed or interrupted copy
                // never leaves a truncated exe where the shortcuts point.
                string staged = ExePath + ".new";
                using (var file = File.Create(staged)) payload.CopyTo(file);
                // A process that has just exited can hold its file for a moment longer.
                Retry(() =>
                {
                    File.Delete(ExePath);
                    File.Move(staged, ExePath);
                });
            }
        }

        static void Retry(Action action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { action(); return; }
                catch (IOException) { if (attempt == 9) throw; }
                catch (UnauthorizedAccessException) { if (attempt == 9) throw; }
                Thread.Sleep(300);
            }
        }

        static void CreateShortcut(string linkPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic link = shell.CreateShortcut(linkPath);
                try
                {
                    link.TargetPath = ExePath;
                    link.WorkingDirectory = InstallDir;
                    link.Description = "Performance overlay for games";
                    link.IconLocation = ExePath + ",0";
                    link.Save();
                }
                finally { Marshal.FinalReleaseComObject((object)link); }
            }
            finally { Marshal.FinalReleaseComObject((object)shell); }
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        static string Quote(string path) { return "\"" + path + "\""; }
    }

    sealed class SetupForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        static readonly Color Surface = Color.FromArgb(0x20, 0x20, 0x20);
        static readonly Color Raised = Color.FromArgb(0x2D, 0x2D, 0x2D);
        static readonly Color Line = Color.FromArgb(0x5A, 0x5A, 0x5A);
        static readonly Color Muted = Color.FromArgb(0xC5, 0xC5, 0xC5);

        readonly CheckBox startup, desktop, launch;
        readonly Button primary, secondary, remove;
        readonly Label status;
        Action retry;
        bool finished, busy;

        public SetupForm()
        {
            bool installed = Installer.IsInstalled;

            var title = new Label { Text = Installer.AppName, Font = new Font("Segoe UI Semibold", 18f), AutoSize = true, Location = new Point(24, 20) };
            var subtitle = new Label { Text = "Performance overlay for games  ·  version " + Installer.Version, ForeColor = Muted, AutoSize = true, Location = new Point(27, 60) };
            var where = new Label
            {
                Text = "Installs for your Windows account only, so no administrator prompt.\n" + Installer.InstallDir,
                ForeColor = Muted, Location = new Point(27, 92), Size = new Size(430, 40),
            };

            startup = Check("Start " + Installer.AppName + " when I sign in to Windows", 144, installed && Installer.StartsWithWindows);
            desktop = Check("Add a desktop shortcut", 172, installed && Installer.HasDesktopLink);
            launch = Check("Open " + Installer.AppName + " when setup finishes", 200, true);

            status = new Label { ForeColor = Muted, Location = new Point(27, 236), Size = new Size(430, 20) };

            primary = Push(installed ? "Reinstall" : "Install", 262, true);
            secondary = Push("Cancel", 368, false);
            remove = Push("Uninstall", 27, false);
            remove.Visible = installed;
            // After a failure the button reads "Try again" and repeats whichever action failed.
            primary.Click += delegate { if (finished) Close(); else if (retry != null) retry(); else StartInstall(); };
            remove.Click += delegate { StartUninstall(); };
            secondary.Click += delegate { Close(); };

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(484, 320);
            Text = Installer.AppName + " Setup";
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Surface;
            ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); }
            catch (ArgumentException) { }   // run from a network path: keep the default icon
            AcceptButton = primary;
            CancelButton = secondary;
            Controls.AddRange(new Control[] { title, subtitle, where, startup, desktop, launch, status, primary, secondary, remove });
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1, square = 1;   // DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWCP_DONOTROUND
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                DwmSetWindowAttribute(Handle, 33, ref square, sizeof(int));
            }
            catch (DllNotFoundException) { }
        }

        // The uninstaller's last step has to run after this window is gone.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (finished && !Installer.IsInstalled)
            {
                try { Installer.RemoveInstallFolder(); } catch (Exception) { }
            }
        }

        // Closing mid-way would kill the worker with the app half written, so wait for it.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) e.Cancel = true;
            base.OnFormClosing(e);
        }

        void StartInstall()
        {
            var options = new Options { Startup = startup.Checked, Desktop = desktop.Checked, Launch = launch.Checked };
            Run(() => Installer.Install(options), "Installing…", "Installed. Find it in the Start menu as " + Installer.AppName + ".", StartInstall);
        }

        void StartUninstall()
        {
            Run(Installer.Uninstall, "Removing…", Installer.AppName + " has been removed.", StartUninstall);
        }

        void Run(Action work, string working, string done, Action again)
        {
            retry = again;
            busy = true;
            primary.Enabled = secondary.Enabled = remove.Enabled = startup.Enabled = desktop.Enabled = launch.Enabled = false;
            status.ForeColor = Muted;
            status.Text = working;
            Task.Run(work).ContinueWith(t =>
            {
                busy = false;
                bool failed = t.Exception != null;
                status.ForeColor = failed ? Color.FromArgb(0xFF, 0x99, 0xA4) : Color.White;
                status.Text = failed ? "Didn't finish: " + t.Exception.GetBaseException().Message : done;
                finished = !failed;
                primary.Text = failed ? "Try again" : "Close";
                primary.Enabled = true;
                secondary.Enabled = failed;
                secondary.Visible = failed;
                remove.Visible = false;
                startup.Enabled = desktop.Enabled = launch.Enabled = failed;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        CheckBox Check(string text, int top, bool isChecked)
        {
            return new CheckBox { Text = text, Checked = isChecked, AutoSize = true, Location = new Point(28, top), FlatStyle = FlatStyle.Flat };
        }

        Button Push(string text, int left, bool isPrimary)
        {
            var button = new Button
            {
                Text = text, Location = new Point(left, 268), Size = new Size(96, 32), FlatStyle = FlatStyle.Flat,
                BackColor = isPrimary ? Color.White : Raised, ForeColor = isPrimary ? Color.Black : Color.White,
            };
            button.FlatAppearance.BorderColor = isPrimary ? Color.White : Line;
            return button;
        }
    }
}
