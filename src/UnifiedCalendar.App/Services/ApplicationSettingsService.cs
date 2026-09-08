using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.App.Services;

[Flags]
public enum SettingsSection
{
    None = 0,
    Display = 1 << 0,
    Sync = 1 << 1,
    General = 1 << 2,
    Windows = 1 << 3,
    Accounts = 1 << 4,
    ColorRules = 1 << 5,
}

public sealed class ApplicationSettingsChangedEventArgs : EventArgs
{
    public ApplicationSettingsChangedEventArgs(
        AppSettings previous,
        AppSettings current,
        SettingsSection changedSections)
    {
        Previous = previous ?? throw new ArgumentNullException(nameof(previous));
        Current = current ?? throw new ArgumentNullException(nameof(current));
        ChangedSections = changedSections;
    }

    public AppSettings Previous { get; }

    public AppSettings Current { get; }

    public SettingsSection ChangedSections { get; }
}

public interface IApplicationSettingsService
{
    /// <summary>
    /// Occurs synchronously after changed settings have been saved.
    /// </summary>
    /// <remarks>
    /// Subscribers must not block while waiting for calls back into this settings service to complete.
    /// </remarks>
    event EventHandler<ApplicationSettingsChangedEventArgs>? SettingsChanged;

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default);
}

public sealed class ApplicationSettingsService : IApplicationSettingsService
{
    private readonly ISettingsStore _settingsStore;
    private readonly SemaphoreSlim _accessSemaphore = new(1, 1);
    private Task _notificationTail = Task.CompletedTask;

    public ApplicationSettingsService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public event EventHandler<ApplicationSettingsChangedEventArgs>? SettingsChanged;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _accessSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accessSemaphore.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplicationSettingsChangedEventArgs? notification = null;
        Task? precedingNotification = null;
        TaskCompletionSource? notificationCompletion = null;
        await _accessSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var current = update(previous)
                ?? throw new InvalidOperationException("The settings update returned null.");
            var changedSections = GetChangedSections(previous, current);
            if (changedSections == SettingsSection.None)
            {
                return previous;
            }

            await _settingsStore.SaveAsync(current, cancellationToken).ConfigureAwait(false);
            notification = new ApplicationSettingsChangedEventArgs(
                previous,
                current,
                changedSections);
            // Reserve notification order while the access lock still defines save order.
            precedingNotification = _notificationTail;
            notificationCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _notificationTail = notificationCompletion.Task;
        }
        finally
        {
            _accessSemaphore.Release();
        }

        await precedingNotification.ConfigureAwait(false);
        try
        {
            SettingsChanged?.Invoke(this, notification);
            return notification.Current;
        }
        finally
        {
            notificationCompletion.SetResult();
        }
    }

    private static SettingsSection GetChangedSections(AppSettings previous, AppSettings current)
    {
        var changed = SettingsSection.None;
        if (previous.Display != current.Display)
        {
            changed |= SettingsSection.Display;
        }

        if (previous.Sync != current.Sync)
        {
            changed |= SettingsSection.Sync;
        }

        if (previous.General != current.General)
        {
            changed |= SettingsSection.General;
        }

        if (previous.Windows != current.Windows)
        {
            changed |= SettingsSection.Windows;
        }

        if (!previous.Accounts.SequenceEqual(current.Accounts))
        {
            changed |= SettingsSection.Accounts;
        }

        if (!previous.ColorRules.SequenceEqual(current.ColorRules))
        {
            changed |= SettingsSection.ColorRules;
        }

        return changed;
    }
}
