using System.IO;
using Microsoft.Win32;
using UnifiedCalendar.Core;

namespace UnifiedCalendar.App.Shell;

public interface IStartupRegistrationService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

public static class StartupCommandLine
{
    public static string FromExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"'))
        {
            throw new ArgumentException("The executable path cannot contain a quote.", nameof(executablePath));
        }

        return $"\"{Path.GetFullPath(executablePath)}\"";
    }
}

public sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _commandLine;

    public WindowsStartupRegistrationService()
        : this(Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable."))
    {
    }

    public WindowsStartupRegistrationService(string executablePath)
    {
        _commandLine = StartupCommandLine.FromExecutablePath(executablePath);
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppIdentity.StartupRegistryValueName) is string value
                && value.Equals(_commandLine, StringComparison.Ordinal);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key is unavailable.");
        if (enabled)
        {
            key.SetValue(
                AppIdentity.StartupRegistryValueName,
                _commandLine,
                RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppIdentity.StartupRegistryValueName, throwOnMissingValue: false);
        }
    }
}
