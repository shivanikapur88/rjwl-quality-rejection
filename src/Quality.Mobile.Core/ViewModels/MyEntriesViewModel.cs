// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026), then changed:
// history now uses the paged endpoint (GET /api/rejection-entries/page) with "Load more", and says so when the list is
// incomplete because a shop's database could not be read. Previously it showed only the latest 50 entries, as if complete.
using System.Collections.ObjectModel;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class MyEntriesViewModel : BaseViewModel
{
    public const int PageSize = 50;
    private readonly IApiClient _api;
    private readonly ISession _session;
    private readonly INavigator _nav;
    private bool _empty;
    private string? _cursor;
    private string? _incomplete;

    public ObservableCollection<EntryRow> Entries { get; } = new();
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand LoadMoreCommand { get; }

    public bool IsEmpty { get => _empty; private set => Set(ref _empty, value, nameof(IsEmpty)); }
    public bool HasMore => _cursor is not null;

    /// <summary>Shown above the list when one or more shops' entries could not be loaded.</summary>
    public string? IncompleteNotice
    {
        get => _incomplete;
        private set { if (Set(ref _incomplete, value, nameof(IncompleteNotice))) OnPropertyChanged(nameof(IsIncomplete)); }
    }
    public bool IsIncomplete => !string.IsNullOrEmpty(_incomplete);

    public string Heading => _session.IsInspector ? "My entries" : "Entries";

    public MyEntriesViewModel(IApiClient api, ISession session, INavigator nav)
    {
        _api = api;
        _session = session;
        _nav = nav;
        RefreshCommand = new AsyncCommand(LoadAsync);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => HasMore && !IsBusy);
    }

    public override Task OnAppearingAsync()
    {
        OnPropertyChanged(nameof(Heading));
        return LoadAsync();
    }

    private int _loadSeq;

    /// <summary>First page (newest entries). Supersedes any load still running (e.g. a slow "Load more").</summary>
    public Task LoadAsync() => LoadPageAsync(more: false);

    /// <summary>Next page; appends below the rows already shown. Not started while another load is running.</summary>
    public Task LoadMoreAsync() => HasMore && !IsBusy ? LoadPageAsync(more: true) : Task.CompletedTask;

    private async Task LoadPageAsync(bool more)
    {
        // Only the latest load may change the list, the cursor or IsBusy: a "Load more" answering after a refresh
        // would otherwise append an old page below the new first page and replace the cursor.
        var seq = Interlocked.Increment(ref _loadSeq);
        var cursor = more ? _cursor : null;
        IsBusy = true;
        ErrorMessage = null;
        LoadMoreCommand.RaiseCanExecuteChanged();
        try
        {
            var r = await _api.GetEntriesPageAsync(cursor, PageSize);
            if (seq != Volatile.Read(ref _loadSeq)) return;
            if (!r.IsSuccess) { ErrorMessage = r.Error!.Message; return; }
            if (!more) Entries.Clear();
            Apply(r.Value!);
        }
        catch (Exception)
        {
            if (seq == Volatile.Read(ref _loadSeq)) ErrorMessage = GenericError;
        }
        finally
        {
            if (seq == Volatile.Read(ref _loadSeq)) { IsBusy = false; LoadMoreCommand.RaiseCanExecuteChanged(); }
        }
    }

    private void Apply(EntryPage page)
    {
        foreach (var e in page.Items) Entries.Add(Row(e));
        _cursor = page.NextCursor;
        OnPropertyChanged(nameof(HasMore));
        LoadMoreCommand.RaiseCanExecuteChanged();
        IncompleteNotice = page.UnavailableShops.Count == 0 ? null
            : $"Entries from {string.Join(", ", page.UnavailableShops)} could not be loaded right now, so this list is incomplete. Pull down to try again.";
        IsEmpty = Entries.Count == 0;
    }

    public static EntryRow Row(EntryDto e) => new(e.Id,
        $"{e.PhenomenonName} × {e.Quantity}",
        $"{e.ShopCode} › {e.StageName} › {e.MachineCode}",
        $"{EntryText.When(e)} · {e.InspectorEmployeeCode}" + (string.IsNullOrWhiteSpace(e.Remarks) ? "" : " · " + e.Remarks),
        e, EntryText.Badge(e));

    /// <summary>Round 2: tapping an entry opens its detail (where its inspector can ask for a correction).</summary>
    public Task OpenAsync(EntryRow row) => row.Dto is null ? Task.CompletedTask
        : _nav.GoToAsync(Routes.EntryDetail, new Dictionary<string, object> { ["entry"] = row.Dto });
}
