using OeXYZ.Core;
using OeXYZ.Updater;
using System.Diagnostics;

namespace OeXYZ.ConsoleClient;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        Application.SetDefaultFont(AppFonts.Create(9F));
        if (TryRunUpdateHelper(args)) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => CrashReporter.Report(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            CrashReporter.Report(eventArgs.ExceptionObject as Exception ?? new Exception("Unknown application error."));
        Application.Run(new MainForm(args));
    }

    private static bool TryRunUpdateHelper(string[] args)
    {
        if (args.Length != 4 || !string.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            if (!int.TryParse(args[1], out int processId) || processId <= 0)
                throw new ArgumentException("The updater received an invalid process ID.");
            try { Process.GetProcessById(processId).WaitForExit(30_000); }
            catch (ArgumentException) { }
            PreparedUpdate prepared = UpdateInstaller.ValidateStage(args[2]);
            _ = UpdateInstaller.ApplyWithRollback(prepared, args[3]);
            string application = Path.Combine(Path.GetFullPath(args[3]), "OeXYZ Console Client.exe");
            Process.Start(new ProcessStartInfo(application) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            string fallback = Path.Combine(Path.GetTempPath(), "OeXYZ-update-error.log");
            File.WriteAllText(fallback, SensitiveDataRedactor.RedactText(exception.ToString()));
            BrandMessageBox.Show($"The update could not be applied. The previous files were restored when possible.\n\n{exception.Message}\n\nLog: {fallback}",
                "OeXYZ update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return true;
    }
}

internal static class AppPaths
{
    private static readonly ApplicationPaths Paths = ApplicationPaths.Resolve();
    public static string Root => Paths.Root;
    public static string Profiles => Paths.Profiles;
    public static string ProtectedAccounts => Paths.ProtectedAccounts;
    public static string Logs => Paths.Logs;
    public static string Diagnostics => Paths.Diagnostics;
}

internal static class CrashReporter
{
    public static void Report(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            string path = Path.Combine(AppPaths.Logs, "application-crash.log");
            string safeException = SensitiveDataRedactor.RedactText(exception.ToString());
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{safeException}{Environment.NewLine}{Environment.NewLine}");
            BrandMessageBox.Show(
                $"OeXYZ caught an unexpected error. No account token was written to this log.\n\n{exception.Message}\n\nLog: {path}",
                "OeXYZ Console Client",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
        }
    }
}
