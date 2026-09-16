using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace FFXIII2SaveEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        TryWriteStartupTrace("Managed OnStartup entered.");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            base.OnStartup(e);

            string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
            string[] required =
            {
                "item_names.tsv",
                "item_catalog.tsv",
                "character_accessory_effects.tsv",
                "monster_names.tsv",
                "monster_level_caps.tsv",
                "monster_traits.tsv",
                "monster_skills.tsv"
            };

            string[] missing = required
                .Where(name => !File.Exists(Path.Combine(assets, name)))
                .ToArray();

            if (missing.Length > 0)
            {
                throw new FileNotFoundException(
                    "The editor was started without its required Assets folder. " +
                    "Do not copy only FFXIII2SaveEditor.exe. Run the EXE from the complete publish folder. " +
                    "Missing: " + string.Join(", ", missing));
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            TryWriteStartupTrace("Main window shown successfully.");
        }
        catch (Exception ex)
        {
            ReportStartupFailure("Application startup failed.", ex);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportStartupFailure("Unexpected application error.", e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            TryWriteCrashLog("Unhandled application error.", ex);
    }


    private static void TryWriteStartupTrace(string message)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FFXIII2SaveEditor",
                "startup-trace.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:O} | {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never prevent startup.
        }
    }

    private static void ReportStartupFailure(string heading, Exception ex)
    {
        string logPath = TryWriteCrashLog(heading, ex);
        string details =
            $"{heading}\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
            $"A diagnostic log was written to:\n{logPath}";

        try
        {
            MessageBox.Show(details, "FFXIII-2 Save Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Nothing else can be displayed if WPF itself failed before the UI was available.
        }
    }

    private static string TryWriteCrashLog(string heading, Exception ex)
    {
        string text = BuildCrashText(heading, ex);
        string primary = Path.Combine(AppContext.BaseDirectory, "startup-error.log");

        try
        {
            File.WriteAllText(primary, text, Encoding.UTF8);
            return primary;
        }
        catch
        {
            string fallback = Path.Combine(Path.GetTempPath(), "FFXIII2SaveEditor-startup-error.log");
            try
            {
                File.WriteAllText(fallback, text, Encoding.UTF8);
                return fallback;
            }
            catch
            {
                return "(log could not be written)";
            }
        }
    }

    private static string BuildCrashText(string heading, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FFXIII-2 PS3 Save Editor v1.9.33");
        sb.AppendLine(heading);
        sb.AppendLine($"Time: {DateTime.Now:O}");
        sb.AppendLine($"Base directory: {AppContext.BaseDirectory}");
        sb.AppendLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine();
        sb.AppendLine(ex.ToString());
        return sb.ToString();
    }
}
