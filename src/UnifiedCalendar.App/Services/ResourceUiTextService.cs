using System.Globalization;
using System.Resources;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.App.Services;

public sealed class ResourceUiTextService : IUiTextService
{
    private static readonly CultureInfo UiCulture = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly ResourceManager Resources = new(
        "UnifiedCalendar.App.Resources.Strings",
        typeof(ResourceUiTextService).Assembly);

    public string Get(string resourceKey, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ArgumentNullException.ThrowIfNull(arguments);

        var format = Resources.GetString(resourceKey, UiCulture)
            ?? throw new MissingManifestResourceException($"UI resource '{resourceKey}' was not found.");
        var values = arguments.Select(FormatArgument).ToArray();
        return string.Format(UiCulture, format, values);
    }

    public string Format(UiText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.IsLiteral
            ? text.Literal!
            : Get(text.ResourceKey!, text.Arguments.ToArray());
    }

    public bool Contains(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        return Resources.GetString(resourceKey, UiCulture) is not null;
    }

    private object? FormatArgument(object? argument) => argument is UiText text
        ? Format(text)
        : argument;
}
