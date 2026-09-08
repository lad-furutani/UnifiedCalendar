namespace UnifiedCalendar.Core;

public static class AppIdentity
{
    public const string ProductName = "UnifiedCalendar";
    public const string ProductDirectoryName = ProductName;
    public const string StartupRegistryValueName = ProductName;
    // Permanent application/uninstaller contract. Never change after release; keep the installer definition in sync.
    public const string RunningApplicationMutexName = @"Local\UnifiedCalendar.Running";
    public static string InstanceMutexPrefix { get; } = $@"Local\{ProductName}.";
    public static string ActivationPipePrefix { get; } = $"{ProductName}.Activation.";
}
