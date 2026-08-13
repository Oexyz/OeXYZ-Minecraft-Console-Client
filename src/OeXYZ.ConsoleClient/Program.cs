using OeXYZ.Core;

namespace OeXYZ.ConsoleClient;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => CrashReporter.Report(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            CrashReporter.Report(eventArgs.ExceptionObject as Exception ?? new Exception("Unknown application error."));
        Application.Run(new MainForm(args));
    }
}

internal static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OeXYZ",
        "ConsoleClient");
    public static readonly string Profiles = Path.Combine(Root, "profiles.json");
    public static readonly string ProtectedAccounts = Path.Combine(Root, "accounts.bin");
    public static readonly string Logs = Path.Combine(Root, "logs");
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
            MessageBox.Show(
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
