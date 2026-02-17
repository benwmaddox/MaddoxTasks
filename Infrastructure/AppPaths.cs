using System.Runtime.InteropServices;
using System.Text.Json;

namespace MaddoxTasks.Infrastructure;

public static class AppPaths
{
    private const string AppName = "MaddoxTasks";
    private const string DatabaseFileName = "MaddoxTasks.db";
    private const string SettingsFileName = "MaddoxTasks.json";

    public static string ResolveDatabasePath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return EnsureDirectoryAndGetAbsolutePath(ExpandAndNormalize(overridePath, Environment.CurrentDirectory));
        }

        var configuredPath = TryReadConfiguredDatabasePath();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return EnsureDirectoryAndGetAbsolutePath(GetDefaultDatabasePath());
    }

    private static string? TryReadConfiguredDatabasePath()
    {
        foreach (var settingsFile in GetSettingsFileCandidates())
        {
            if (!File.Exists(settingsFile))
            {
                continue;
            }

            var rawJson = File.ReadAllText(settingsFile);
            var settings = JsonSerializer.Deserialize(rawJson, JsonDefaults.Context.MaddoxTasksSettings);

            if (string.IsNullOrWhiteSpace(settings?.DatabasePath))
            {
                continue;
            }

            var settingsDirectory = Path.GetDirectoryName(settingsFile) ?? Environment.CurrentDirectory;
            return EnsureDirectoryAndGetAbsolutePath(ExpandAndNormalize(settings.DatabasePath, settingsDirectory));
        }

        return null;
    }

    private static IEnumerable<string> GetSettingsFileCandidates()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, SettingsFileName),
            Path.Combine(AppContext.BaseDirectory, SettingsFileName),
            Path.Combine(GetDefaultConfigDirectory(), SettingsFileName)
        };

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetDefaultDatabasePath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrWhiteSpace(oneDrive))
            {
                return Path.Combine(oneDrive, AppName, DatabaseFileName);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName,
                DatabaseFileName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppName, DatabaseFileName);
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdgDataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdgDataHome = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(xdgDataHome, AppName, DatabaseFileName);
    }

    private static string GetDefaultConfigDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppName);
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdgConfigHome = Path.Combine(home, ".config");
        }

        return Path.Combine(xdgConfigHome, AppName);
    }

    private static string ExpandAndNormalize(string path, string baseDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }

    private static string EnsureDirectoryAndGetAbsolutePath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return fullPath;
    }
}

public sealed class MaddoxTasksSettings
{
    public string? DatabasePath { get; init; }
}
