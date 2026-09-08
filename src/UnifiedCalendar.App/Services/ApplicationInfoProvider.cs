using System.Reflection;
using UnifiedCalendar.Core;

namespace UnifiedCalendar.App.Services;

public sealed record ApplicationInfo(
    string ProductName,
    string Version,
    DateTimeOffset BuildTimeUtc);

public interface IApplicationInfoProvider
{
    ApplicationInfo Get();
}

public sealed class AssemblyApplicationInfoProvider : IApplicationInfoProvider
{
    public const string BuildTimestampMetadataKey = "BuildTimestampUtc";

    private readonly Assembly _assembly;

    public AssemblyApplicationInfoProvider()
        : this(typeof(AssemblyApplicationInfoProvider).Assembly)
    {
    }

    internal AssemblyApplicationInfoProvider(Assembly assembly)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    }

    public ApplicationInfo Get()
    {
        var version = _assembly.GetName().Version;
        var versionText = version is null ? "1.0.0" : version.ToString(3);
        var timestampText = _assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(
                BuildTimestampMetadataKey,
                StringComparison.Ordinal))
            ?.Value;
        if (!DateTimeOffset.TryParse(
                timestampText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var buildTimeUtc))
        {
            throw new InvalidOperationException("The application build timestamp metadata is unavailable.");
        }

        return new ApplicationInfo(AppIdentity.ProductName, versionText, buildTimeUtc);
    }
}
