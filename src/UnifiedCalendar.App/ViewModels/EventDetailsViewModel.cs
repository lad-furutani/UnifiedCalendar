using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.App.ViewModels;

public sealed partial class EventDetailsViewModel : ObservableObject
{
    private readonly IUiTextService _textService;
    private readonly IExternalUriLauncher _uriLauncher;
    private PresentedEvent _presented;

    public EventDetailsViewModel(
        PresentedEvent presented,
        IUiTextService textService,
        IExternalUriLauncher uriLauncher)
    {
        _presented = presented ?? throw new ArgumentNullException(nameof(presented));
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _uriLauncher = uriLauncher ?? throw new ArgumentNullException(nameof(uriLauncher));
        OpenMeetingCommand = new AsyncRelayCommand(OpenMeetingAsync, () => MeetingUri is not null);
        OpenSourceCommand = new AsyncRelayCommand(OpenSourceAsync, () => SourceDetailUri is not null);
        OpenContentUriCommand = new AsyncRelayCommand<Uri>(OpenContentUriAsync);
        UpdateFrom(presented);
    }

    public EventKey Key => _presented.Key;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _dateTime = string.Empty;

    [ObservableProperty]
    private string? _location;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string? _response;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _calendarName = string.Empty;

    [ObservableProperty]
    private Uri? _meetingUri;

    [ObservableProperty]
    private Uri? _sourceDetailUri;

    public IAsyncRelayCommand OpenMeetingCommand { get; }

    public IAsyncRelayCommand OpenSourceCommand { get; }

    public IAsyncRelayCommand<Uri> OpenContentUriCommand { get; }

    public string LocationLabel => _textService.Get(UiResourceKeys.DetailLocation);

    public string MeetingLabel => _textService.Get(UiResourceKeys.DetailMeeting);

    public string DescriptionLabel => _textService.Get(UiResourceKeys.DetailDescription);

    public string SourceLabel => _textService.Get(UiResourceKeys.DetailSource);

    public string CalendarLabel => _textService.Get(UiResourceKeys.DetailCalendar);

    public string ResponseLabel => _textService.Get(UiResourceKeys.DetailResponse);

    public string OpenSourceLabel => _textService.Get(UiResourceKeys.DetailOpenSource);

    public string? MeetingUriText => MeetingUri?.AbsoluteUri;

    public void UpdateFrom(PresentedEvent presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (presented.Key != Key)
        {
            throw new ArgumentException("Details can only be updated from the same event key.", nameof(presented));
        }

        _presented = presented;
        Title = _textService.Format(presented.Title);
        DateTime = _textService.Format(presented.DetailDateTime);
        Location = NullIfWhiteSpace(presented.Source.Location);
        Description = NullIfWhiteSpace(presented.Source.DescriptionPlainText);
        Response = presented.DetailResponseText is null
            ? null
            : _textService.Format(presented.DetailResponseText);
        Provider = _textService.Get(presented.Key.Provider == ProviderKind.Google
            ? UiResourceKeys.ProviderGoogle
            : UiResourceKeys.ProviderMicrosoft);
        CalendarName = presented.Source.CalendarName;
        MeetingUri = presented.Source.MeetingUri;
        SourceDetailUri = presented.Source.SourceDetailUri;
        OpenMeetingCommand.NotifyCanExecuteChanged();
        OpenSourceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(MeetingUriText));
    }

    public Task OpenContentUriAsync(Uri? uri) => uri is null
        ? Task.CompletedTask
        : _uriLauncher.OpenAsync(uri);

    private Task OpenMeetingAsync(CancellationToken cancellationToken) => MeetingUri is null
        ? Task.CompletedTask
        : _uriLauncher.OpenAsync(MeetingUri, cancellationToken);

    private Task OpenSourceAsync(CancellationToken cancellationToken) => SourceDetailUri is null
        ? Task.CompletedTask
        : _uriLauncher.OpenAsync(SourceDetailUri, cancellationToken);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

}
