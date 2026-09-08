using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.App.Services;

public interface IUiTextService
{
    string Get(string resourceKey, params object?[] arguments);

    string Format(UiText text);

    bool Contains(string resourceKey);
}
