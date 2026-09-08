using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.App.ViewModels;

public abstract class TimelineItemViewModel : ObservableObject
{
    protected TimelineItemViewModel(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        Identity = identity;
    }

    public string Identity { get; }

    public abstract bool IsFocusable { get; }
}

public sealed class DayHeaderItemViewModel : TimelineItemViewModel
{
    private string _header;

    public DayHeaderItemViewModel(DateOnly date, string header)
        : base($"D:{date:yyyy-MM-dd}")
    {
        Date = date;
        _header = header ?? throw new ArgumentNullException(nameof(header));
    }

    public DateOnly Date { get; }

    public override bool IsFocusable => false;

    public string Header
    {
        get => _header;
        private set => SetProperty(ref _header, value);
    }

    public void UpdateHeader(string header) => Header = header;
}

public sealed class EventRowViewModel : TimelineItemViewModel
{
    private readonly IUiTextService _textService;
    private readonly BrushCache _brushCache;
    private readonly IExternalUriLauncher _uriLauncher;
    private PresentedEvent _presented;
    private string _title = string.Empty;
    private string _listTime = string.Empty;
    private string _tooltip = string.Empty;
    private string? _attentionText;
    private Brush _background = Brushes.Transparent;
    private Brush _foreground = Brushes.Black;

    public EventRowViewModel(
        PresentedEvent presented,
        IUiTextService textService,
        BrushCache brushCache,
        IExternalUriLauncher uriLauncher,
        Action<EventKey> openDetails)
        : base($"E:{presented?.StableId ?? throw new ArgumentNullException(nameof(presented))}")
    {
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _brushCache = brushCache ?? throw new ArgumentNullException(nameof(brushCache));
        _uriLauncher = uriLauncher ?? throw new ArgumentNullException(nameof(uriLauncher));
        ArgumentNullException.ThrowIfNull(openDetails);
        _presented = presented;
        OpenDetailsCommand = new RelayCommand(() => openDetails(Key));
        OpenMeetingCommand = new AsyncRelayCommand(OpenMeetingAsync, () => HasMeetingUri);
        OpenSourceCommand = new AsyncRelayCommand(OpenSourceAsync, () => HasSourceDetailUri);
        UpdateFrom(presented);
    }

    public EventKey Key => _presented.Key;

    public override bool IsFocusable => true;

    public DateOnly GroupDate => _presented.GroupDate;

    public PresentedEvent Presented => _presented;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string ListTime
    {
        get => _listTime;
        private set => SetProperty(ref _listTime, value);
    }

    public string Tooltip
    {
        get => _tooltip;
        private set => SetProperty(ref _tooltip, value);
    }

    public string? AttentionText
    {
        get => _attentionText;
        private set => SetProperty(ref _attentionText, value);
    }

    public Brush Background
    {
        get => _background;
        private set => SetProperty(ref _background, value);
    }

    public Brush Foreground
    {
        get => _foreground;
        private set => SetProperty(ref _foreground, value);
    }

    public string ProviderMark => _presented.Key.Provider == ProviderKind.Google ? "G" : "M";

    public bool HasMeetingUri => _presented.Source.MeetingUri is not null;

    public bool HasSourceDetailUri => _presented.Source.SourceDetailUri is not null;

    public string MeetingLabel => _textService.Get(UiResourceKeys.DetailMeeting);

    public ICommand OpenDetailsCommand { get; }

    public IAsyncRelayCommand OpenMeetingCommand { get; }

    public IAsyncRelayCommand OpenSourceCommand { get; }

    public void UpdateFrom(PresentedEvent presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (presented.Key != Key)
        {
            throw new ArgumentException("An event row can only be updated from the same event key.", nameof(presented));
        }

        _presented = presented;
        Title = _textService.Format(presented.Title);
        ListTime = _textService.Format(presented.ListTime);
        Tooltip = _textService.Format(presented.Tooltip);
        AttentionText = presented.AttentionResponseText is null
            ? null
            : _textService.Format(presented.AttentionResponseText);
        Background = _brushCache.GetEventBackground(
            presented.BackgroundColor,
            presented.ElapsedColor,
            presented.RemainingColor,
            presented.Progress);
        Foreground = _brushCache.GetSolid(presented.ForegroundColor);
        OnPropertyChanged(nameof(GroupDate));
        OnPropertyChanged(nameof(ProviderMark));
        OnPropertyChanged(nameof(HasMeetingUri));
        OnPropertyChanged(nameof(HasSourceDetailUri));
        OpenMeetingCommand.NotifyCanExecuteChanged();
        OpenSourceCommand.NotifyCanExecuteChanged();
    }

    private Task OpenMeetingAsync(CancellationToken cancellationToken) => _presented.Source.MeetingUri is null
        ? Task.CompletedTask
        : _uriLauncher.OpenAsync(_presented.Source.MeetingUri, cancellationToken);

    private Task OpenSourceAsync(CancellationToken cancellationToken) => _presented.Source.SourceDetailUri is null
        ? Task.CompletedTask
        : _uriLauncher.OpenAsync(_presented.Source.SourceDetailUri, cancellationToken);
}
