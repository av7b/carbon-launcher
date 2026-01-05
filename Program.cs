// This source is a bit buggy, but should work fine. Proper integration of missing features & improvements will be made throughout the time.
// This source is subject to change.
// If you're confused on how to use this source, then please
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CarbonLauncher
{
    public partial class LauncherForm : Form
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isPresent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;

        private readonly string placeId;
        private readonly string ticket;
        private readonly bool use2020;

        private readonly string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Carbon");
        private readonly string exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Carbon", "Carbon.exe");

        private string ClientFolder => Path.Combine(appData, use2020 ? "2020" : "2016");
        private string ClientExe => Path.Combine(ClientFolder, "BBPlayerBeta.exe");

        private Process gameProcess;
        private Thread antiCheatThread;
        private ProgressBar progress;
        private Label status;
        private bool FirstRun = true;
        private bool Installing = true;

        public LauncherForm(string placeId, string ticket, bool use2020)
        {
            this.placeId = placeId;
            this.ticket = ticket;
            this.use2020 = use2020;
            InitializeComponent();
            this.Load += (s, e) => Task.Run((Action)StartLauncher);
        }

        private async void StartLauncher()
        {

            await EnsureLauncherCopied();
            RegisterProtocol();
            await InstallBothClients();

            if (!string.IsNullOrEmpty(placeId) && !string.IsNullOrEmpty(ticket))
                LaunchGameWithAntiCheat();
            else
                this.Close();
        }

        private async Task EnsureLauncherCopied()
        {
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            string current = Process.GetCurrentProcess().MainModule.FileName;
            if (File.Exists(exePath) && File.GetLastWriteTime(current) <= File.GetLastWriteTime(exePath)) return;
            File.Copy(current, exePath, true);
        }

        private void RegisterProtocol()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\bbclient"))
                {
                    key.SetValue("", "URL:Carbon Protocol");
                    key.SetValue("URL Protocol", "");
                    using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exePath}\",1");
                    using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exePath}\" \"%1\"");
                }
            }
            catch { }
        }

        private async Task InstallBothClients()
        {
            bool has2016 = File.Exists(Path.Combine(appData, "2016", "BBPlayerBeta.exe")); // change the bbplayerbeta.exe if you're using a different client name
            bool has2020 = File.Exists(Path.Combine(appData, "2020", "BBPlayerBeta.exe")); // change the bbplayerbeta.exe if you're using a different client name

            if (has2016 && has2020) return;

            using (var wc = new WebClient())
            {
                if (!has2016)
                {
                    UpdateStatus("Installing 2016 client...");
                    await wc.DownloadFileTaskAsync("https://lureon.fit/clientings/16client.zip", Path.Combine(appData, "16client.zip"));
                    Extract(Path.Combine(appData, "16client.zip"), Path.Combine(appData, "2016"));
                }
                if (!has2020)
                {
                    UpdateStatus("Installing 2020 client...");
                    await wc.DownloadFileTaskAsync("https://lureon.fit/clientings/20client.zip", Path.Combine(appData, "20client.zip"));
                    Extract(Path.Combine(appData, "20client.zip"), Path.Combine(appData, "2020"));
                }
            }
        }

        private void Extract(string zip, string dest)
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            ZipFile.ExtractToDirectory(zip, dest);
            File.Delete(zip);
        }

        private void LaunchGameWithAntiCheat()
        {
            UpdateStatus("Launching Carbon...");

            string authUrl = "https://lureon.fit/Login/Negotiate.ashx";
            string joinUrl = use2020
              ? $"http://lureon.fit/game/PlaceLauncher.ashx?placeid={placeId}&ticket={ticket}&2020=true"
              : $"http://lureon.fit/game/PlaceLauncher.ashx?placeid={placeId}&ticket={ticket}";

            var psi = new ProcessStartInfo
            {
                FileName = ClientExe,
                Arguments = $"-a \"{authUrl}\" -j \"{joinUrl}\" -t \"{ticket}\"",
                WorkingDirectory = ClientFolder,
                UseShellExecute = true
            };

            gameProcess = Process.Start(psi);
            antiCheatThread = new Thread(() => AntiCheatMonitor(gameProcess))
            {
                IsBackground = true,
                Name = "carbonmonoxide"
            };
            antiCheatThread.Start();

            Task.Delay(3000).ContinueWith(_ => BeginInvoke((MethodInvoker)Close));
        }

        private void AntiCheatMonitor(Process proc)
        {
            while (!proc.HasExited)
            {
                try
                {
                    proc.Refresh();

                    // check for debuggers (any debugger really)
                    bool isDebuggerPresent = false;
                    if (CheckRemoteDebuggerPresent(proc.Handle, ref isDebuggerPresent) && isDebuggerPresent)
                    {
                        KillnNotify("Debugger detected attempting to attach to the client process. You have been kicked from the session");
                        return;
                    }

                    // check for process memory access handles (cheat engine etc.)
                    // if an external tool has opened the process with memory read or write rights, this will succeed.
                    if (IsProcessMemoryAccessed(proc.Id))
                    {
                        KillnNotify("External application detected accessing client memory. You have been kicked from the session.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    
                }
                Thread.Sleep(500);
            }
        }

        private void KillnNotify(string reason)
        {
            if (!gameProcess.HasExited)
            {
                try { gameProcess.Kill(); } catch { }
            }

            // use BeginInvoke since this is called from a non ui thread (antiCheatThread)
            BeginInvoke((MethodInvoker)(() =>
            {
                MessageBox.Show($"Carbon Monoxide has detected a third party application. Reason: {reason}\n\nYou will be exited from this application.", "Carbon - Carbon Monoxide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Application.Exit();
            }));
        }

        private bool IsProcessMemoryAccessed(int processId) // below lists what this part of the anticheat actually does
        {
            // attempt to open the process with read or write access rights if the process is already opened by another tool with these rights, or if the tool is actively using them, this check attempts to confirm external access
            uint desiredAccess = PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION;
            IntPtr handle = OpenProcess(desiredAccess, false, processId);

            if (handle != IntPtr.Zero)
            {
                // if we get a handle successfully, then we can check
                CloseHandle(handle);
                // this is a weak check, as it only proves the launcher can get these rights a more stronger but complex check would be to monitor for specific handles
                // however, for most simple external tools, trying to open these rights can sometimes indicate an issue if other checks are failing
                // for now, this check is most effective when trying to get a handle with rights that another process is trying to control or is actively denying us
                return false;
            }
            int lastError = Marshal.GetLastWin32Error();
            if (lastError == 5)
            {
                // access is denied. This may indicate another process has opened the handle with exclusive access rights, or it can be due to security orintegrity levels
                // for a launcher monitoring its own child process, this is often a good indicator of tampering, though it can be a false positive
                return true;
            }
            // please note you may add further more attachments and implementations to the anticheat system.
            return false;
        }
        private void UpdateStatus(string text)
        {
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                if (this.InvokeRequired)
                    this.BeginInvoke((MethodInvoker)(() => status.Text = text));
                else
                    status.Text = text;
            }
        }
        private void Cancel()
        {
            // no proper integration right now, just close the form.
            this.Close();
        }
        private void InitializeComponent()
        {
            this.Text = "Carbon Launcher";
            this.ClientSize = new System.Drawing.Size(400, 200);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = System.Drawing.Color.White;
            this.StartPosition = FormStartPosition.CenterScreen;

            var logo = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = 200,
                Height = 100,
                Top = 20,
                Left = (this.ClientSize.Width - 200) / 2,
            };

            // man this ide is a pain in the ass
            using (var ms = new MemoryStream(CarbonLauncher.Properties.Resources.carbon_logo))
            {
                logo.Image = Image.FromStream(ms);
            }

            this.Controls.Add(logo);

            progress = new ProgressBar
            {
                Top = 145,
                Left = 20,
                Width = this.ClientSize.Width - 40,
                Height = 20,
                Style = ProgressBarStyle.Continuous
            };
            this.Controls.Add(progress);

            status = new Label
            {
                Top = 100,
                Left = 20,
                Width = this.ClientSize.Width - 40,
                Height = 60,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Text = FirstRun ? "Installing Carbon..." : "Launching Carbon..."
            };
            this.Controls.Add(status);

            var cancel = new Button
            {
                Text = "Cancel",
                Top = 170,
                Left = (this.ClientSize.Width - 80) / 2,
                Width = 80,
                Height = 25
            };
            cancel.Click += (s, e) =>
            {
                if (Installing)
                    Cancel();
                else
                    this.Close();
            };
            this.Controls.Add(cancel);
        }

        static class Program
        {
            [STAThread]
            static void Main(string[] args)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string place = null, ticket = null;
                bool use2020 = false;

                if (args.Length > 0)
                {
                    string url = args[0];
                    int hash = url.IndexOf('#');
                    if (hash >= 0) url = url.Substring(0, hash);

                    if (url.StartsWith("bbclient://", StringComparison.OrdinalIgnoreCase)) // idk when im gonna change this protocol
                    {
                        var uri = new Uri(url);
                        var q = HttpUtility.ParseQueryString(uri.Query);
                        place = q["place"] ?? q["placeId"];
                        ticket = q["ticket"];
                        use2020 = q["2020"] == "true" || q["year"] == "2020";
                    }
                }

                Application.Run(new LauncherForm(place, ticket, use2020));
            }
        }
    }
}