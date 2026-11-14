using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO.Compression;
using Microsoft.Win32;

namespace BubbaBloxLauncher
{
    public partial class Launcher : Form
    {
        public const string Scheme = "okakiiclient";
        public const string BaseUrl = "https://www.okakii.lol";
        public static readonly string ClientsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Okakii", "Clients");
        public const string ExeName = "OkakiiPlayerBeta.exe";
        public const string DownloadBase = "https://okakii.lol/download";

        // The old anti-cheat ("guard") is no longer shipped; launchers clean up what earlier versions installed.
        private static readonly string OldGuardDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Okakii", "Guard");

        private static readonly string GuardDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Okakii", "AntiCheat");

        private readonly string placeId;
        private readonly string ticket;
        private readonly string targetYear;
        private readonly string yearFolder;
        private string targetExePath;
        private readonly string launcherPath;
        private Label status;
        private ClassicProgressBar bar;

        public Launcher(string placeId, string ticket, string year, bool preview = false)
        {
            this.placeId = placeId;
            this.ticket = ticket;
            this.targetYear = string.IsNullOrEmpty(year) ? "2017" : year;

            string appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Okakii");
            launcherPath = Path.Combine(appdata, "OkakiiLauncher.exe");

            yearFolder = targetYear == "2021" ? "2021" : targetYear == "2020" ? "2020" : targetYear == "2018" ? "2018" : targetYear == "2015" ? "2015" : "2017";
            targetExePath = ResolveClientExe(yearFolder);

            if (preview) { InitUI(); return; } // --preview: window only, touches nothing
            RegisterProtocolIfNeeded();
            InitUI();
            Start();
        }

        private static string ResolveClientExe(string yearFolder)
        {
            string perUser = Path.Combine(ClientsRoot, yearFolder, ExeName);
            string[] candidates =
            {
                perUser,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Clients", yearFolder, ExeName),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return perUser;
        }

        private void RegisterProtocolIfNeeded()
        {
            try
            {
                using (var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Scheme}"))
                {
                    if (existing != null) return;
                }
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}"))
                {
                    key.SetValue("", "URL:Okakii Protocol");
                    key.SetValue("URL Protocol", "");
                    using (RegistryKey cmd = key.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue("", "\"" + launcherPath + "\" \"%1\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not register okakiiclient:// protocol: " + ex.Message, "Okakii", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Borderless classic launcher dialog: logo, status line, green progress bar.
        private void InitUI()
        {
            this.Text = "Okakii";
            this.FormBorderStyle = FormBorderStyle.None;
            this.ClientSize = new System.Drawing.Size(520, 320);
            this.BackColor = System.Drawing.Color.White;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ShowInTaskbar = true;
            this.KeyPreview = true;
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };
            this.Paint += (s, e) =>
            {
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 200, 200)))
                    e.Graphics.DrawRectangle(pen, 0, 0, this.ClientSize.Width - 1, this.ClientSize.Height - 1);
            };
            this.MouseDown += DragWindow;

            var logo = LoadLogo();
            if (logo != null)
            {
                this.Icon = System.Drawing.Icon.FromHandle(((System.Drawing.Bitmap)logo).GetHicon());
                var pic = new PictureBox
                {
                    Width = 104,
                    Height = 104,
                    Image = logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = System.Drawing.Color.Transparent
                };
                pic.Left = (this.ClientSize.Width - pic.Width) / 2;
                pic.Top = 58;
                pic.MouseDown += DragWindow;
                this.Controls.Add(pic);
            }

            status = new Label
            {
                Left = 20,
                Top = 196,
                Width = this.ClientSize.Width - 40,
                Height = 26,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Text = "Connecting to Okakii ...",
                Font = new System.Drawing.Font("Segoe UI", 11.25F),
                ForeColor = System.Drawing.Color.FromArgb(34, 34, 34),
                BackColor = System.Drawing.Color.Transparent
            };
            status.MouseDown += DragWindow;
            this.Controls.Add(status);

            bar = new ClassicProgressBar { Left = 30, Top = 236, Width = this.ClientSize.Width - 60, Height = 20 };
            this.Controls.Add(bar);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(this.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }

        private void SetProgress(int percent)
        {
            try
            {
                if (bar == null || this.IsDisposed) return;
                this.Invoke((MethodInvoker)(() => { if (!this.IsDisposed) { if (percent < 0) bar.SetMarquee(); else bar.SetValue(percent); } }));
            }
            catch { }
        }

        private static System.Drawing.Image LoadLogo()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string name = null;
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                if (name == null) return null;
                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (var tmp = System.Drawing.Image.FromStream(stream))
                        return new System.Drawing.Bitmap(tmp);
                }
            }
            catch { return null; }
        }

        private void Start()
        {
            try
            {
                this.Show();
                this.Refresh();

                try { Task.Run(() => RemoveOldGuard()); } catch { }

                if (string.IsNullOrEmpty(placeId) || string.IsNullOrEmpty(ticket))
                {
                    MessageBox.Show("Okakii launcher is set up. Press Play on a game at okakii.lol to join.",
                        "Okakii", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.Close();
                    return;
                }

                Launch();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, "Okakii - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        // Bump together with dist/OkakiiLauncher.version on the site whenever a new launcher ships.
        // Installed launchers compare against it on every launch and replace themselves.
        public const int LauncherVersion = 8;
        public const string UpdatedFlag = "--updated";

        private bool TrySelfUpdate()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), UpdatedFlag) >= 0) return false;
            string dir = Path.GetDirectoryName(launcherPath);
            string fresh = Path.Combine(dir, "OkakiiLauncher.new.exe");
            string old = Path.Combine(dir, "OkakiiLauncher.old.exe");
            try
            {
                string remoteText;
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    remoteText = http.GetStringAsync($"{DownloadBase}/OkakiiLauncher.version").GetAwaiter().GetResult().Trim();
                if (!int.TryParse(remoteText, out int remote) || remote <= LauncherVersion) return false;

                SetStatus("Updating Okakii ...");
                using (var client = new System.Net.WebClient())
                    client.DownloadFile($"{DownloadBase}/OkakiiLauncher.exe?v={remote}", fresh);

                try { File.Delete(old); } catch { }
                bool movedOld = false;
                if (File.Exists(launcherPath))
                {
                    File.Move(launcherPath, old);
                    movedOld = true;
                }
                try
                {
                    File.Move(fresh, launcherPath);
                }
                catch
                {
                    if (movedOld && !File.Exists(launcherPath)) File.Move(old, launcherPath);
                    throw;
                }

                var args = Environment.GetCommandLineArgs();
                var forward = new System.Text.StringBuilder();
                for (int i = 1; i < args.Length; i++) forward.Append('"').Append(args[i]).Append("\" ");
                forward.Append(UpdatedFlag);
                Process.Start(new ProcessStartInfo(launcherPath, forward.ToString()) { UseShellExecute = false });
                return true;
            }
            catch
            {
                try { File.Delete(fresh); } catch { }
                return false;
            }
        }

        private void Launch()
        {
            Task.Run(() =>
            {
                try
                {
                    if (TrySelfUpdate())
                    {
                        try { this.Invoke((MethodInvoker)(() => { if (!this.IsDisposed) this.Close(); })); } catch { }
                        return;
                    }

                    string remoteVersion = GetRemoteVersion();
                    bool needInstall = !File.Exists(targetExePath)
                        || (remoteVersion != null && ReadLocalVersion() != remoteVersion);

                    if (needInstall)
                    {
                        EnsureClientInstalled(remoteVersion);
                        if (remoteVersion != null)
                            WriteLocalVersion(remoteVersion);
                    }

                    if (!File.Exists(targetExePath))
                    {
                        ShowErrorAndClose($"Couldn't install the client.\nExpected:\n{targetExePath}");
                        return;
                    }

                    ClearStaleAssetCache();
                    EnsureGuard();
                    LaunchClient();
                }
                catch (Exception ex)
                {
                    ShowErrorAndClose("Error: " + ex.Message);
                }
            });
        }

        private string VersionFilePath()
        {
            return Path.Combine(ClientsRoot, yearFolder, ".okakii_version");
        }

        private string ReadLocalVersion()
        {
            try
            {
                string p = VersionFilePath();
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch { return null; }
        }

        private void WriteLocalVersion(string version)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(VersionFilePath()));
                File.WriteAllText(VersionFilePath(), version);
            }
            catch { }
        }

        private string GetRemoteVersion()
        {
            try
            {
                using (var client = new System.Net.WebClient())
                    return client.DownloadString($"{DownloadBase}/OkakiiClient{yearFolder}.version").Trim();
            }
            catch { return null; }
        }

        private void EnsureClientInstalled(string version)
        {
            SetStatus("Downloading Okakii ...");
            SetProgress(0);

            Directory.CreateDirectory(ClientsRoot);
            string zipUrl = $"{DownloadBase}/OkakiiClient{yearFolder}.zip?v={Uri.EscapeDataString(version ?? DateTime.UtcNow.Ticks.ToString())}";
            string tmpZip = Path.Combine(Path.GetTempPath(), $"OkakiiClient{yearFolder}.{Guid.NewGuid():N}.zip");

            using (var client = new System.Net.WebClient())
            {
                long lastShown = -1;
                client.DownloadProgressChanged += (s, e) =>
                {
                    if (e.ProgressPercentage == lastShown) return;
                    lastShown = e.ProgressPercentage;
                    double mb = e.TotalBytesToReceive / 1048576.0;
                    SetStatus(mb > 0
                        ? $"Downloading Okakii ... {e.ProgressPercentage}%"
                        : $"Downloading Okakii ... {e.BytesReceived / 1048576.0:0} MB");
                    if (mb > 0) SetProgress(e.ProgressPercentage);
                };
                var done = client.DownloadFileTaskAsync(new Uri(zipUrl), tmpZip);
                done.GetAwaiter().GetResult();
            }

            SetStatus("Installing Okakii ...");
            SetProgress(-1);
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, ClientsRoot, true);
            }
            finally
            {
                try { File.Delete(tmpZip); } catch { }
            }

            targetExePath = ResolveClientExe(yearFolder);
        }

        private void LaunchClient()
        {
            SetStatus("Starting Okakii ...");

            string yearFlag = targetYear == "2021" ? "&2021=true" : targetYear == "2020" ? "&2020=true" : targetYear == "2018" ? "&2018=true" : "";
            string joinUrl = $"{BaseUrl}/game/PlaceLauncher.ashx?placeid={placeId}&ticket={ticket}{yearFlag}";
            string authUrl = $"{BaseUrl}/Login/Negotiate.ashx";
            string arguments = $"-a \"{authUrl}\" -j \"{joinUrl}\" -t \"{ticket}\"";

            var psi = new ProcessStartInfo
            {
                FileName = targetExePath,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetExePath)
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                ShowErrorAndClose("Failed to launch the client.");
                return;
            }

            StartGuard(proc.Id);
            try { proc.WaitForInputIdle(3000); FocusGameWindow(proc); } catch { }
            Thread.Sleep(800);

            try { this.Invoke((MethodInvoker)(() => { if (!this.IsDisposed) this.Close(); })); } catch { }
        }

        private void SetStatus(string text)
        {
            try
            {
                if (status == null || this.IsDisposed) return;
                this.Invoke((MethodInvoker)(() => { if (!this.IsDisposed) status.Text = text; }));
            }
            catch { }
        }

        private void ShowErrorAndClose(string message)
        {
            try
            {
                this.Invoke((MethodInvoker)(() =>
                {
                    MessageBox.Show(message, "Okakii - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (!this.IsDisposed) this.Close();
                }));
            }
            catch { }
        }

        // The client caches downloaded assets forever. Bump this when the site starts serving fixed
        // versions of assets players may already have cached (e.g. animations), to wipe them once.
        private const string AssetCacheEpoch = "1";

        private static void ClearStaleAssetCache()
        {
            try
            {
                string marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Okakii", ".asset-cache-epoch");
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == AssetCacheEpoch) return;
                if (Process.GetProcessesByName("OkakiiPlayerBeta").Length > 0 || Process.GetProcessesByName("RobloxPlayerBeta").Length > 0) return;
                string cache = Path.Combine(Path.GetTempPath(), "Roblox", "http");
                if (Directory.Exists(cache))
                    foreach (var f in Directory.GetFiles(cache))
                        try { File.Delete(f); } catch { }
                Directory.CreateDirectory(Path.GetDirectoryName(marker));
                File.WriteAllText(marker, AssetCacheEpoch);
            }
            catch { }
        }

        private void EnsureGuard()
        {
            string exe = Path.Combine(GuardDir, "OkakiiGuard.exe");
            string verFile = Path.Combine(GuardDir, ".version");
            try
            {
                string remote;
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    remote = http.GetStringAsync($"{DownloadBase}/OkakiiGuard.version?t={DateTime.UtcNow.Ticks}").GetAwaiter().GetResult().Trim();
                string local = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : null;
                if (File.Exists(exe) && local == remote) return;

                SetStatus("Updating anti-cheat ...");
                Directory.CreateDirectory(GuardDir);
                string tmp = exe + ".new";
                using (var client = new System.Net.WebClient())
                    client.DownloadFile($"{DownloadBase}/OkakiiGuard.exe?v={Uri.EscapeDataString(remote)}", tmp);
                foreach (var p in Process.GetProcessesByName("OkakiiGuard"))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
                File.Copy(tmp, exe, true);
                File.Delete(tmp);
                File.WriteAllText(verFile, remote);
            }
            catch { }
        }

        private void StartGuard(int gamePid)
        {
            string exe = Path.Combine(GuardDir, "OkakiiGuard.exe");
            if (!File.Exists(exe)) return;
            try
            {
                string args = $"--parent {gamePid} --ticket \"{ticket}\" --place {placeId} --year {yearFolder} " +
                              $"--clientdir \"{Path.GetDirectoryName(targetExePath)}\" --launcher {Environment.ProcessId}";
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = GuardDir });
                MarkPcSession();
            }
            catch { }
        }

        // Tell the backend this account is playing on PC with the guard, so a game server will kick it
        // if the guard later stops. Only the Windows launcher does this; mobile and Mac never flag PC.
        private void MarkPcSession()
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create($"{BaseUrl}/guard/v1/pc-session");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 5000;
                req.Headers.Add("Cookie", ".ROBLOSECURITY=" + ticket);
                using (var s = req.GetRequestStream()) { var b = System.Text.Encoding.UTF8.GetBytes("{}"); s.Write(b, 0, b.Length); }
                using (var resp = req.GetResponse()) { }
            }
            catch { }
        }

        private static void RemoveOldGuard()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("OkakiiAntiCheat"))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                    try { p.Dispose(); } catch { }
                }
                if (Directory.Exists(OldGuardDir)) Directory.Delete(OldGuardDir, true);
            }
            catch { }
        }

        private void FocusGameWindow(Process p)
        {
            try
            {
                for (int i = 0; i < 10 && !p.HasExited; i++)
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                    Thread.Sleep(100);
                }
            }
            catch { }
        }

        internal static class NativeMethods
        {
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        }

        static class Program
        {
            [STAThread]
            static void Main(string[] args)
            {
                Application.EnableVisualStyles();
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.SetCompatibleTextRenderingDefault(false);

                int snap = Array.IndexOf(args, "--snapshot");
                if (snap >= 0 && snap + 1 < args.Length)
                {
                    // Render the dialog to a PNG (for design review) without showing or installing anything.
                    var f = new Launcher(null, null, "2021", preview: true);
                    f.Opacity = 0;
                    f.ShowInTaskbar = false;
                    f.Show();
                    for (int k = 0; k < 20; k++) { Application.DoEvents(); Thread.Sleep(15); }
                    using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(args[snap + 1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    f.Close();
                    return;
                }
                if (Array.IndexOf(args, "--preview") >= 0)
                {
                    Application.Run(new Launcher(null, null, "2021", preview: true));
                    return;
                }

                EnsureExistence();

                string placeId = null, ticket = null, year = "2017";

                if (args.Length > 0 && args[0].StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = args[0];
                    int qi = uri.IndexOf('?');
                    if (qi >= 0)
                    {
                        foreach (var kv in uri.Substring(qi + 1).Split('&'))
                        {
                            int eq = kv.IndexOf('=');
                            if (eq < 0) continue;
                            string k = kv.Substring(0, eq);
                            string v = Uri.UnescapeDataString(kv.Substring(eq + 1).TrimEnd('/'));
                            if (k == "place") placeId = v;
                            else if (k == "ticket") ticket = v;
                            else if (k == "year") year = v;
                        }
                    }
                }

                Application.Run(new Launcher(placeId, ticket, year));
            }

            private static void EnsureExistence()
            {
                string appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Okakii");
                if (!Directory.Exists(appdata)) Directory.CreateDirectory(appdata);
                string launcher = Path.Combine(appdata, "OkakiiLauncher.exe");
                try { File.Delete(Path.Combine(appdata, "OkakiiLauncher.old.exe")); } catch { }
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    if (!File.Exists(launcher) || File.GetLastWriteTime(exe) > File.GetLastWriteTime(launcher))
                        File.Copy(exe, launcher, true);
                }
                catch { }
            }
        }
    }
}
