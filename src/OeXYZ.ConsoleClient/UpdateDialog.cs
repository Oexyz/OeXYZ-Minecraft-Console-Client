using System.Diagnostics;
using OeXYZ.Updater;

namespace OeXYZ.ConsoleClient;

internal sealed class UpdateDialog : Form
{
    private readonly Label heading = Theme.Heading("Checking for updates", 16F);
    private readonly Label message = new();
    private readonly Label versions = new();
    private readonly ProgressBar progress = new();
    private readonly Button download = Theme.Button("Download & install", 190);
    private readonly Button releasePage = Theme.Button("Open release page", 150);
    private readonly Button close = Theme.Button("Close", 90);
    private readonly CancellationTokenSource lifetime = new();
    private UpdateCheckResult? result;

    private UpdateDialog()
    {
        Text = "OeXYZ Updates";
        ClientSize = new Size(560, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;

        heading.Location = new Point(24, 22);
        heading.Size = new Size(510, 34);
        message.Location = new Point(24, 70);
        message.Size = new Size(510, 68);
        message.ForeColor = Color.FromArgb(198, 210, 225);
        message.Font = AppFonts.Create(10F);
        versions.Location = new Point(24, 142);
        versions.Size = new Size(510, 48);
        versions.ForeColor = Theme.Muted;
        versions.Font = AppFonts.Create(9F);
        progress.Location = new Point(24, 204);
        progress.Size = new Size(512, 8);
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 24;

        download.Location = new Point(24, 244);
        download.Visible = false;
        Theme.Primary(download);
        releasePage.Location = new Point(222, 244);
        releasePage.Visible = false;
        close.Location = new Point(446, 244);
        close.DialogResult = DialogResult.Cancel;
        close.Enabled = true;
        download.Click += async (_, _) => await DownloadAsync();
        releasePage.Click += (_, _) => OpenReleasePage();

        Controls.Add(heading);
        Controls.Add(message);
        Controls.Add(versions);
        Controls.Add(progress);
        Controls.Add(download);
        Controls.Add(releasePage);
        Controls.Add(close);
        CancelButton = close;
        Shown += async (_, _) =>
        {
            Theme.ApplyDarkTitleBar(this);
            await CheckAsync();
        };
        FormClosed += (_, _) => lifetime.Cancel();
    }

    public static void ShowFor(IWin32Window owner)
    {
        using UpdateDialog dialog = new();
        dialog.ShowDialog(owner);
    }

    private async Task CheckAsync()
    {
        CancellationToken cancellationToken = lifetime.Token;
        message.Text = "Contacting the configured GitHub repository over HTTPS...";
        try
        {
            result = await GitHubUpdateService.CheckAsync(cancellationToken);
            if (IsDisposed) return;
            progress.Visible = false;
            versions.Text = $"INSTALLED  {Display(result.CurrentVersion)}\nLATEST     {Display(result.LatestVersion)}";
            if (!result.IsUpdateAvailable)
            {
                bool newerThanPublished = result.IsCurrentNewer;
                heading.Text = newerThanPublished ? "This build is newer" : "You are up to date";
                heading.ForeColor = Theme.Green;
                message.Text = newerThanPublished
                    ? "This installation is newer than the latest public OeXYZ release."
                    : "This installation matches the newest published OeXYZ release.";
                releasePage.Visible = true;
                return;
            }
            heading.Text = $"OeXYZ {Display(result.LatestVersion)} is available";
            heading.ForeColor = Theme.BlueBright;
            message.Text = "Download the Windows ZIP. OeXYZ will accept it only when its SHA-256 hash matches the release manifest.";
            download.Visible = true;
            releasePage.Visible = true;
        }
        catch (UpdateSourceNotConfiguredException)
        {
            if (!IsDisposed)
                ShowError("No update source configured",
                    "This developer build has no repository metadata. Official release builds configure it automatically.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
                ShowError("Update check failed", exception.Message + " Nothing was downloaded or installed.");
        }
    }

    private async Task DownloadAsync()
    {
        if (result is null) return;
        if (BrandMessageBox.Show(this,
                $"Download, verify and install OeXYZ {Display(result.LatestVersion)}?\n\n" +
                "The app will close, keep a rollback backup, replace both architecture-matched executables and restart. Updates are never silent.",
                "Confirm OeXYZ update", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        CancellationToken cancellationToken = lifetime.Token;
        try
        {
            UseWaitCursor = true;
            download.Enabled = false;
            close.Text = "Cancel";
            progress.Visible = true;
            heading.Text = "Downloading and verifying";
            message.Text = "The archive is written to a temporary file until its SHA-256 checksum has been verified.";
            string updateRoot = Path.Combine(AppPaths.Root, "updates", $"v{Display(result.LatestVersion)}");
            string archive = Path.Combine(updateRoot, result.AssetName);
            string stage = Path.Combine(updateRoot, "stage");
            Directory.CreateDirectory(updateRoot);
            await GitHubUpdateService.DownloadVerifiedAsync(result, archive, cancellationToken);
            if (Directory.Exists(stage))
                throw new IOException("A staged update already exists. Remove the updates folder and retry.");
            UpdateInstaller.ExtractVerifiedArchive(archive, stage);
            _ = UpdateInstaller.ValidateStage(stage);
            if (IsDisposed) return;
            heading.Text = "Verified update prepared";
            heading.ForeColor = Theme.Green;
            message.Text = "OeXYZ will close, install the staged files with rollback protection, and restart.";
            string helper = Path.Combine(Path.GetTempPath(), $"OeXYZ-UpdateHelper-{Guid.NewGuid():N}.exe");
            File.Copy(Application.ExecutablePath, helper, overwrite: false);
            Process.Start(new ProcessStartInfo(helper)
            {
                UseShellExecute = true,
                ArgumentList =
                {
                    "--apply-update",
                    Environment.ProcessId.ToString(),
                    stage,
                    AppContext.BaseDirectory
                }
            });
            Application.Exit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsDisposed) ShowError("Download rejected", exception.Message + " Nothing was installed.");
        }
        finally
        {
            if (!IsDisposed)
            {
                progress.Visible = false;
                UseWaitCursor = false;
                download.Enabled = true;
                close.Text = "Close";
            }
        }
    }

    private void OpenReleasePage()
    {
        if (result is null) return;
        Process.Start(new ProcessStartInfo(result.ReleasePage) { UseShellExecute = true });
    }

    private void ShowError(string title, string detail)
    {
        progress.Visible = false;
        heading.Text = title;
        heading.ForeColor = Theme.Danger;
        message.Text = detail;
        versions.Text = string.Empty;
        download.Visible = false;
    }

    private static string Display(Version value) =>
        value.Build >= 0 ? $"{value.Major}.{value.Minor}.{value.Build}" : $"{value.Major}.{value.Minor}";

    protected override void Dispose(bool disposing)
    {
        if (disposing) lifetime.Dispose();
        base.Dispose(disposing);
    }
}
