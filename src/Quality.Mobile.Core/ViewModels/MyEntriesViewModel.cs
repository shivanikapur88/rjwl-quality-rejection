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

    public MyEntriesViewModel(IApiClient api, ISession session)
    {
        _api = api;
        _session = session;
        RefreshCommand = new AsyncCommand(LoadAsync);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => HasMore && !IsBusy);
    }

    public override Task OnAppearingAsync()
    {
        OnPropertyChanged(nameof(Heading));
        return LoadAsync();
    }

    /// <summary>First page (newest entries).</summary>
    public Task LoadAsync() => RunBusyAsync(async () =>
    {
        var r = await _api.GetEntriesPageAsync(null, PageSize);
        if (!r.IsSuccess) { ErrorMessage = r.Error!.Message; return; }
        Entries.Clear();
        Apply(r.Value!);
    });

    /// <summary>Next page; appends below the rows already shown.</summary>
    public Task LoadMoreAsync() => HasMore ? RunBusyAsync(async () =>
    {
        var r = await _api.GetEntriesPageAsync(_cursor, PageSize);
        if (!r.IsSuccess) { ErrorMessage = r.Error!.Message; return; }
        Apply(r.Value!);
    }) : Task.CompletedTask;

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
        $"{e.ServerCreatedAtUtc.ToLocalTime():dd MMM yyyy HH:mm} · {e.InspectorEmployeeCode}" + (string.IsNullOrWhiteSpace(e.Remarks) ? "" : " · " + e.Remarks));
}
