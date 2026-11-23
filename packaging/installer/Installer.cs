using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// NAI Files setup — a small signed bootstrapper around the MSIX.
//
// It runs as the invoking user (no UAC at launch), downloads the MSIX, verifies
// it against the SHA-256 compiled into this executable, asks Windows for
// elevation ONCE to install the NAI Sovereign CA trust (root ->
// LocalMachine\Root, intermediate -> CA, leaf -> TrustedPeople), then installs
// the MSIX for the CURRENT USER with Add-AppxPackage and offers to launch it.
// This mirrors the NAI OS and NAI Cards flow: no user-run PowerShell, one UAC
// confirmation.
//
// `--trust` is the elevated helper mode of the same executable.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--trust") >= 0)
        {
            Environment.Exit(TrustInstaller.Run());
            return;
        }

        // TLS 1.2 for the package download (WebClient's default is old).
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new InstallerForm(args))
        {
            Application.Run(form);
        }
    }
}

internal static class TrustInstaller
{
    internal static int Run()
    {
        string dir = null;
        try
        {
            dir = Path.Combine(Path.GetTempPath(), "nai-files-trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string root = Extract("NAI-Root.cer", dir);
            string inter = Extract("NAI-Inter.cer", dir);
            string leaf = Extract("NAI-Leaf.cer", dir);

            var certificate = new X509Certificate2(leaf);
            if (!string.Equals(certificate.Subject, InstallerConfig.ExpectedSubject, StringComparison.Ordinal))
            {
                MessageBox.Show("Unexpected NAI Files signing certificate: " + certificate.Subject, InstallerConfig.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // Import through the X509Store API. Never spawn
            // `certutil.exe -addstore Root` from an installer: that command line
            // matches the Ceprolad/rogue-root-certificate heuristic (Defender).
            AddToMachineStore(root, StoreName.Root);
            AddToMachineStore(inter, StoreName.CertificateAuthority);
            AddToMachineStore(leaf, StoreName.TrustedPeople);

            var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!chain.Build(certificate))
            {
                var sb = new StringBuilder();
                foreach (X509ChainStatus status in chain.ChainStatus)
                    sb.AppendLine(status.Status + ": " + status.StatusInformation);
                MessageBox.Show("The NAI trust chain could not be verified.\n" + sb, InstallerConfig.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Installing the NAI certificate authority trust failed.\n" + ex.Message, InstallerConfig.ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }
    }

    private static string Extract(string resourceName, string dir)
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (stream == null) throw new InvalidOperationException("Missing embedded certificate: " + resourceName);
            string path = Path.Combine(dir, resourceName);
            using (var file = File.Create(path)) stream.CopyTo(file);
            return path;
        }
    }

    private static void AddToMachineStore(string path, StoreName storeName)
    {
        using (var certificate = new X509Certificate2(path))
        using (var store = new X509Store(storeName, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            if (!store.Certificates.Contains(certificate)) store.Add(certificate);
            store.Close();
        }
    }
}

internal sealed class InstallerForm : Form
{
    private readonly string[] args;
    private readonly Label status;
    private readonly ProgressBar progress;
    private string downloadedMsix;

    public InstallerForm(string[] args)
    {
        this.args = args;
        Text = InstallerConfig.ProductName + " Setup";
        ClientSize = new Size(470, 128);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        status = new Label();
        status.AutoSize = false;
        status.SetBounds(18, 20, 434, 40);
        status.Text = "Preparing installation...";
        Controls.Add(status);

        progress = new ProgressBar();
        progress.SetBounds(18, 72, 434, 20);
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 30;
        Controls.Add(progress);

        Shown += delegate { StartInstall(); };
    }

    private void StartInstall()
    {
        var thread = new Thread(RunInstall);
        thread.IsBackground = true;
        thread.Start();
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) BeginInvoke(new Action<string>(SetStatus), text);
        else status.Text = text;
    }

    private void SetProgress(int percent)
    {
        if (InvokeRequired) BeginInvoke(new Action<int>(SetProgress), percent);
        else
        {
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = Math.Max(0, Math.Min(100, percent));
        }
    }

    private void RunInstall()
    {
        try
        {
            string msix = ResolveMsix();
            SetStatus("Verifying the package...");
            VerifyHash(msix);

            StopRunningApp();
            SetStatus("Installing NAI Sovereign CA trust (confirm the UAC prompt)...");
            int trust = ElevateTrust();
            if (trust == 1223)
            {
                Fail("Installation was cancelled in the UAC prompt.");
                return;
            }
            if (trust != 0)
            {
                Fail("The certificate authority trust could not be installed.");
                return;
            }

            SetStatus("Installing NAI Files for this user...");
            string output;
            int code = InstallPackage(msix, out output);
            if (code != 0)
            {
                Fail("MSIX installation failed.\n\n" + output);
                return;
            }

            SetStatus("Done.");
            if (downloadedMsix != null)
            {
                try { File.Delete(downloadedMsix); }
                catch { }
            }
            if (AskLaunch())
            {
                LaunchApp();
            }
            CloseOnUi();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private string ResolveMsix()
    {
        string explicitPath = ArgValue("--msix");
        if (!string.IsNullOrEmpty(explicitPath)) return RequireFile(explicitPath);

        string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string[] candidates = new string[]
        {
            Path.Combine(exeDir, InstallerConfig.MsixFileName),
            Path.Combine(exeDir, "msix", InstallerConfig.MsixFileName),
            Path.Combine(exeDir, "..", "msix", InstallerConfig.MsixFileName)
        };
        foreach (string candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        if (!string.IsNullOrEmpty(InstallerConfig.BaseUrl))
        {
            string baseUrl = InstallerConfig.BaseUrl.TrimEnd('/') + "/";
            string target = Path.Combine(Path.GetTempPath(), InstallerConfig.MsixFileName);
            SetStatus("Downloading NAI Files...");
            Download(baseUrl + InstallerConfig.MsixFileName, target);
            downloadedMsix = target;
            return target;
        }

        throw new FileNotFoundException(
            "Package " + InstallerConfig.MsixFileName + " was not found next to the installer. " +
            "Download it from the same location as the installer.");
    }

    private static string RequireFile(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Package not found: " + full);
        return full;
    }

    private void Download(string url, string target)
    {
        using (var client = new WebClient())
        {
            client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
            {
                SetProgress(e.ProgressPercentage);
            };
            var done = new ManualResetEvent(false);
            Exception error = null;
            client.DownloadFileCompleted += delegate(object sender, AsyncCompletedEventArgs e)
            {
                error = e.Error;
                done.Set();
            };
            client.DownloadFileAsync(new Uri(url), target);
            done.WaitOne();
            if (error != null) throw error;
        }
    }

    private void VerifyHash(string msix)
    {
        string expected = InstallerConfig.ExpectedSha256;
        if (string.IsNullOrEmpty(expected))
        {
            string shaFile = msix + ".sha256";
            if (!File.Exists(shaFile)) return;
            expected = File.ReadAllText(shaFile).Trim().Split(' ')[0];
        }

        string actual;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(msix))
        {
            actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        if (!string.Equals(expected.Trim().ToLowerInvariant(), actual, StringComparison.Ordinal))
            throw new InvalidDataException("The package checksum does not match — download it again.");
    }

    private static void StopRunningApp()
    {
        foreach (string name in new string[] { "Files.App", "NAI.Files" })
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try { process.CloseMainWindow(); if (!process.WaitForExit(4000)) process.Kill(); }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static int ElevateTrust()
    {
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--trust");
        start.UseShellExecute = true;
        start.Verb = "runas";
        try
        {
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Win32Exception ex)
        {
            if (ex.NativeErrorCode == 1223) return 1223;
            throw;
        }
    }

    private static int InstallPackage(string msix, out string output)
    {
        string script =
            "try { Add-AppxPackage -Path '" + msix.Replace("'", "''") + "' -ForceApplicationShutdown -ErrorAction Stop; exit 0 } " +
            "catch { Write-Output $_.Exception.Message; exit 1 }";
        var start = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using (Process process = Process.Start(start))
        {
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            output = (stdout + "\n" + stderr).Trim();
            return process.ExitCode;
        }
    }

    private static void LaunchApp()
    {
        try
        {
            string script = "$p = Get-AppxPackage -Name '" + InstallerConfig.IdentityName +
                "'; if ($p) { Write-Output ($p.PackageFamilyName) }";
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            using (Process process = Process.Start(start))
            {
                string family = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (family.Length > 0)
                    Process.Start("explorer.exe", "shell:AppsFolder\\" + family + "!" + InstallerConfig.ApplicationId);
            }
        }
        catch { }
    }

    private string ArgValue(string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private bool AskLaunch()
    {
        if (InvokeRequired) return (bool)Invoke(new Func<bool>(AskLaunch));
        return MessageBox.Show(this, "NAI Files is installed. Launch it now?", InstallerConfig.ProductName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
    }

    private void ShowMessage(string text, MessageBoxIcon icon)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, MessageBoxIcon>(ShowMessage), text, icon);
            return;
        }
        MessageBox.Show(this, text, InstallerConfig.ProductName, MessageBoxButtons.OK, icon);
    }

    private void Fail(string message)
    {
        Environment.ExitCode = 1;
        SetStatus("Installation failed.");
        ShowMessage(message, MessageBoxIcon.Error);
        CloseOnUi();
    }

    private void CloseOnUi()
    {
        if (InvokeRequired) BeginInvoke(new Action(Close));
        else Close();
    }
}
