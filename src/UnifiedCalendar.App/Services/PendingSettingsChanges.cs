using System.Windows;

namespace UnifiedCalendar.App.Services;

public interface IPendingSettingsChanges
{
    event EventHandler? StateChanged;

    bool IsDirty { get; }

    Task ApplyAsync(CancellationToken cancellationToken = default);

    void Discard();
}

public interface IValidatedPendingSettingsChanges
{
    bool CanApply { get; }
}

public sealed class NoPendingSettingsChanges : IPendingSettingsChanges
{
    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public bool IsDirty => false;

    public Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Discard()
    {
    }
}

public enum SettingsExitDecision
{
    Apply,
    Discard,
}

public interface ISettingsConfirmationService
{
    bool ConfirmDiscard();

    SettingsExitDecision ConfirmApplicationExit();
}

public interface ISettingsResetConfirmationService
{
    bool ConfirmReset();
}

public interface IColorRuleDeleteConfirmationService
{
    bool ConfirmDelete(string ruleName);
}

public interface IAccountDeleteConfirmationService
{
    bool ConfirmDelete(string accountDisplayName);
}

public sealed class MessageBoxSettingsConfirmationService
    : ISettingsConfirmationService,
      ISettingsResetConfirmationService,
      IColorRuleDeleteConfirmationService,
      IAccountDeleteConfirmationService
{
    private readonly IUiTextService _textService;

    public MessageBoxSettingsConfirmationService(IUiTextService textService)
    {
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
    }

    public bool ConfirmDiscard() => MessageBox.Show(
        _textService.Get(UiResourceKeys.SettingsDiscardPrompt),
        _textService.Get(UiResourceKeys.SettingsTitle),
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public SettingsExitDecision ConfirmApplicationExit() => MessageBox.Show(
        _textService.Get(UiResourceKeys.SettingsExitPrompt),
        _textService.Get(UiResourceKeys.SettingsTitle),
        MessageBoxButton.YesNo,
        MessageBoxImage.Question,
        MessageBoxResult.Yes) == MessageBoxResult.Yes
        ? SettingsExitDecision.Apply
        : SettingsExitDecision.Discard;

    public bool ConfirmReset() => MessageBox.Show(
        _textService.Get(UiResourceKeys.SettingsResetPrompt),
        _textService.Get(UiResourceKeys.SettingsTitle),
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmDelete(string ruleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        return MessageBox.Show(
            _textService.Get(UiResourceKeys.SettingsColorRulesDeletePrompt, ruleName),
            _textService.Get(UiResourceKeys.SettingsTitle),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    bool IAccountDeleteConfirmationService.ConfirmDelete(string accountDisplayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDisplayName);
        return MessageBox.Show(
            _textService.Get(UiResourceKeys.SettingsAccountsDeletePrompt, accountDisplayName),
            _textService.Get(UiResourceKeys.SettingsTitle),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
