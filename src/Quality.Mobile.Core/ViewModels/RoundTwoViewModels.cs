// Round 2 (new, not recovered): entry detail + correction request (#1), correction requests list (#1),
// shift summary (#4) and machine QR scan (#8).
using System.Collections.ObjectModel;
using System.Globalization;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public static class EntryText
{
    public static string When(EntryDto e) =>
        $"{e.ServerCreatedAtUtc.ToLocalTime():dd MMM yyyy HH:mm}" + (e.ProductionDate is DateOnly d && e.ShiftCode is { } s ? $" · Shift {s}, {d:dd MMM}" : "");

    public static string? Badge(EntryDto e) => e.IsReversed ? "Reversed (corrected)"
        : e.CorrectionStatus == "Pending" ? "Correction requested"
        : e.CorrectsEntryId is long id ? $"Correction of entry #{id}"
        : null;
}

/// <summary>Detail of one entry. The inspector who recorded it can ask for a correction here.</summary>
public sealed class EntryDetailViewModel : BaseViewModel
{
    private readonly IApiClient _api; private readonly INavigator _nav; private readonly ISession _session; private readonly IDialogs _dialogs;
    private EntryDto? _entry;
    private string _reason = "", _qty = "", _remarks = "";
    private bool _remove = true, _formOpen, _changeRemarks;
    private PhenomenonDto? _phen; private MachineDto? _machine;

    public EntryDetailViewModel(IApiClient api, INavigator nav, ISession session, IDialogs dialogs)
    {
        (_api, _nav, _session, _dialogs) = (api, nav, session, dialogs);
        Title = "Entry";
        OpenFormCommand = new AsyncCommand(OpenFormAsync, () => CanRequest);
        SubmitCommand = new AsyncCommand(SubmitAsync, () => !IsBusy);
        CancelCommand = new AsyncCommand(() => { FormOpen = false; return Task.CompletedTask; });
    }

    public AsyncCommand OpenFormCommand { get; }
    public AsyncCommand SubmitCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public ObservableCollection<PhenomenonDto> Phenomena { get; } = new();
    public ObservableCollection<MachineDto> Machines { get; } = new();

    public string Headline => _entry is null ? "" : $"{_entry.PhenomenonName} × {_entry.Quantity}";
    public string ShopLine => _entry is null ? "" : $"{_entry.ShopCode} › {_entry.StageName} › {_entry.MachineCode} – {_entry.MachineName}";
    public string WhenLine => _entry is null ? "" : EntryText.When(_entry) + $" · {_entry.InspectorEmployeeCode}";
    public string RemarksLine => string.IsNullOrWhiteSpace(_entry?.Remarks) ? "No remarks" : _entry!.Remarks!;
    public string EntryNumber => _entry is null ? "" : $"Entry #{_entry.Id}";
    public string? Badge => _entry is null ? null : EntryText.Badge(_entry);
    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>Only the inspector who recorded it, and only while it is not reversed and has no open request.</summary>
    public bool CanRequest => _entry is not null && _session.IsInspector && _session.User?.UserId == _entry.InspectorId
                              && !_entry.IsReversed && _entry.CorrectionStatus != "Pending" && !_formOpen;

    public bool FormOpen { get => _formOpen; private set { if (Set(ref _formOpen, value, nameof(FormOpen))) { OnPropertyChanged(nameof(CanRequest)); OpenFormCommand.RaiseCanExecuteChanged(); } } }
    public string Reason { get => _reason; set => Set(ref _reason, value ?? "", nameof(Reason)); }
    /// <summary>True: the entry should not count at all. False: it should be replaced by the corrected values.</summary>
    public bool RemoveEntry { get => _remove; set { if (Set(ref _remove, value, nameof(RemoveEntry))) OnPropertyChanged(nameof(CorrectValues)); } }
    public bool CorrectValues { get => !_remove; set => RemoveEntry = !value; }
    public PhenomenonDto? SelectedPhenomenon { get => _phen; set => Set(ref _phen, value, nameof(SelectedPhenomenon)); }
    public MachineDto? SelectedMachine { get => _machine; set => Set(ref _machine, value, nameof(SelectedMachine)); }
    public string QuantityText { get => _qty; set => Set(ref _qty, value ?? "", nameof(QuantityText)); }
    public bool ChangeRemarks { get => _changeRemarks; set => Set(ref _changeRemarks, value, nameof(ChangeRemarks)); }
    public string NewRemarks { get => _remarks; set => Set(ref _remarks, value ?? "", nameof(NewRemarks)); }

    public override async Task OnNavigatedToAsync(IDictionary<string, object> args)
    {
        _entry = Arg<EntryDto>(args, "entry");
        if (_entry is null) { ErrorMessage = "Entry not found."; return; }
        Refresh();
        await RunBusyAsync(async () =>   // fresh copy: status may have changed since the list was loaded
        {
            var r = await _api.GetEntryAsync(_entry.ShopId, _entry.Id);
            if (r.IsSuccess) { _entry = r.Value; Refresh(); }
        });
    }

    private void Refresh()
    {
        foreach (var p in new[] { nameof(Headline), nameof(ShopLine), nameof(WhenLine), nameof(RemarksLine), nameof(EntryNumber), nameof(Badge), nameof(HasBadge), nameof(CanRequest) })
            OnPropertyChanged(p);
        OpenFormCommand.RaiseCanExecuteChanged();
    }

    private Task OpenFormAsync() => RunBusyAsync(async () =>
    {
        var e = _entry!;
        var p = await _api.GetPhenomenaAsync(e.ShopId, e.StageId);
        var m = await _api.GetMachinesAsync(e.ShopId, e.StageId);
        if (!p.IsSuccess || !m.IsSuccess) { ErrorMessage = (p.Error ?? m.Error)!.Message; return; }
        Phenomena.Clear(); foreach (var x in p.Value!) Phenomena.Add(x);
        Machines.Clear(); foreach (var x in m.Value!) Machines.Add(x);
        SelectedPhenomenon = Phenomena.FirstOrDefault(x => x.Id == e.PhenomenonId);
        SelectedMachine = Machines.FirstOrDefault(x => x.Id == e.MachineId);
        QuantityText = e.Quantity.ToString(CultureInfo.InvariantCulture);
        NewRemarks = e.Remarks ?? ""; ChangeRemarks = false; Reason = ""; RemoveEntry = true;
        FormOpen = true;
    });

    /// <summary>Builds the request: only values that differ from the entry are proposed.</summary>
    public CorrectionRequest? BuildRequest(out string? problem)
    {
        problem = null;
        var e = _entry!;
        var reason = Reason.Trim();
        if (reason.Length == 0) { problem = "Give a reason for the correction."; return null; }
        if (RemoveEntry) return new CorrectionRequest(reason);
        int? qty = null;
        if (!int.TryParse(QuantityText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var q) || q < 1 || q > 9999) { problem = "Quantity must be a whole number from 1 to 9999."; return null; }
        if (q != e.Quantity) qty = q;
        int? phen = SelectedPhenomenon is { } sp && sp.Id != e.PhenomenonId ? sp.Id : null;
        int? machine = SelectedMachine is { } sm && sm.Id != e.MachineId ? sm.Id : null;
        string? remarks = ChangeRemarks && (NewRemarks.Trim() != (e.Remarks ?? "")) ? NewRemarks.Trim() : null;
        if (qty is null && phen is null && machine is null && remarks is null) { problem = "Nothing was changed. Change a value, or choose \"Remove this entry\"."; return null; }
        return new CorrectionRequest(reason, machine, phen, qty, remarks);
    }

    public async Task SubmitAsync()
    {
        var req = BuildRequest(out var problem);
        if (req is null) { ErrorMessage = problem; return; }
        await RunBusyAsync(async () =>
        {
            var r = await _api.RequestCorrectionAsync(_entry!.ShopId, _entry.Id, req);
            if (!r.IsSuccess) { ErrorMessage = r.Error!.Message; return; }
            FormOpen = false;
            _entry = _entry with { CorrectionStatus = "Pending" };
            Refresh();
            await _dialogs.AlertAsync("Request sent", "Your supervisor will review the correction. The entry stays as it is until then.");
        });
    }
}

public sealed record CorrectionItem(CorrectionDto Dto, bool CanDecide, bool CanWithdraw)
{
    public string Title => Dto.Entry is { } e ? $"{e.PhenomenonName} × {e.Quantity}  ({e.ShopCode}, entry #{Dto.EntryId})" : $"Entry #{Dto.EntryId} ({Dto.ShopCode})";
    public string Proposal => Dto.IsReverseOnly ? "Remove this entry" : "Change to: " + string.Join(", ", new[]
    {
        Dto.ProposedPhenomenonName is { } p ? $"phenomenon {p}" : null,
        Dto.ProposedQuantity is int q ? $"quantity {q}" : null,
        Dto.ProposedMachineCode is { } m ? $"machine {m}" : null,
        Dto.ProposedRemarks is { } r ? (r.Length == 0 ? "no remarks" : $"remarks \"{r}\"") : null,
    }.Where(x => x is not null));
    public string ReasonLine => $"Reason: {Dto.Reason}";
    public string Meta => $"{Dto.Status} · requested by {Dto.RequestedByCode} {Dto.RequestedAtUtc.ToLocalTime():dd MMM HH:mm}"
        + (Dto.DecidedByCode is { } d ? $" · {Dto.Status.ToLowerInvariant()} by {d}" : "") + (Dto.DecisionNote is { } n ? $": {n}" : "");
}

/// <summary>Supervisors/administrators: requests to decide. Inspectors: their own requests (withdraw while pending).</summary>
public sealed class CorrectionsViewModel : BaseViewModel
{
    private readonly IApiClient _api; private readonly ISession _session; private readonly IDialogs _dialogs;
    private bool _empty; private bool _showDecided;
    public CorrectionsViewModel(IApiClient api, ISession session, IDialogs dialogs)
    {
        (_api, _session, _dialogs) = (api, session, dialogs);
        Title = "Corrections";
        RefreshCommand = new AsyncCommand(LoadAsync);
        ApproveCommand = new AsyncCommand<CorrectionItem>(ApproveAsync);
        RejectCommand = new AsyncCommand<CorrectionItem>(RejectAsync);
        WithdrawCommand = new AsyncCommand<CorrectionItem>(WithdrawAsync);
    }
    public ObservableCollection<CorrectionItem> Items { get; } = new();
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand<CorrectionItem> ApproveCommand { get; }
    public AsyncCommand<CorrectionItem> RejectCommand { get; }
    public AsyncCommand<CorrectionItem> WithdrawCommand { get; }
    public bool IsEmpty { get => _empty; private set => Set(ref _empty, value, nameof(IsEmpty)); }
    public string Heading => _session.IsInspector ? "My correction requests" : "Correction requests";
    public string EmptyText => _showDecided ? "No correction requests." : "No requests are waiting.";
    /// <summary>False: waiting requests only. True: all requests, including decided ones.</summary>
    public bool ShowDecided { get => _showDecided; set { if (Set(ref _showDecided, value, nameof(ShowDecided))) { OnPropertyChanged(nameof(EmptyText)); _ = LoadAsync(); } } }

    public override Task OnAppearingAsync() { OnPropertyChanged(nameof(Heading)); return LoadAsync(); }

    public Task LoadAsync() => RunBusyAsync(async () =>
    {
        var r = await _api.GetCorrectionsAsync(_showDecided ? "all" : "Pending");
        if (!r.IsSuccess) { ErrorMessage = r.Error!.Message; return; }
        Items.Clear();
        var decider = _session.IsSupervisor || _session.IsAdministrator;
        foreach (var c in r.Value!) Items.Add(new CorrectionItem(c, decider && c.Status == "Pending", _session.IsInspector && c.Status == "Pending"));
        IsEmpty = Items.Count == 0;
    });

    private async Task ApproveAsync(CorrectionItem item)
    {
        if (!await _dialogs.ConfirmAsync("Approve correction", $"{item.Title}\n{item.Proposal}\n\nThe entry will be reversed" + (item.Dto.IsReverseOnly ? "." : " and a corrected entry saved."), "Approve", "Cancel")) return;
        await Decide(() => _api.ApproveCorrectionAsync(item.Dto.Id, null));
    }

    private async Task RejectAsync(CorrectionItem item)
    {
        var note = await _dialogs.PromptAsync("Reject correction", "Why is the request rejected? The inspector will see this.", "Reject", "Cancel", "Reason");
        if (note is null) return;
        if (string.IsNullOrWhiteSpace(note)) { ErrorMessage = "Give a reason for rejecting the request."; return; }
        await Decide(() => _api.RejectCorrectionAsync(item.Dto.Id, note.Trim()));
    }

    private async Task WithdrawAsync(CorrectionItem item)
    {
        if (!await _dialogs.ConfirmAsync("Withdraw request", "Withdraw this correction request?", "Withdraw", "Keep")) return;
        await Decide(() => _api.WithdrawCorrectionAsync(item.Dto.Id));
    }

    private async Task Decide(Func<Task<ApiResult<CorrectionDto>>> call)
    {
        await RunBusyAsync(async () => { var r = await call(); if (!r.IsSuccess) ErrorMessage = r.Error!.Message; });
        var keep = ErrorMessage;
        await LoadAsync();   // always refresh: someone else may have decided it ("already decided")
        if (keep is not null) ErrorMessage = keep;
    }
}

public sealed record SummaryLine(string Name, string Detail, string Value);

/// <summary>Feature #4: rejected parts for one production day and shift (supervisors and administrators).</summary>
public sealed class SummaryViewModel : BaseViewModel
{
    private readonly IApiClient _api;
    private DateOnly _day; private DateOnly _today; private Option? _shift; private string _total = "–", _entries = "", _incomplete = "";
    public SummaryViewModel(IApiClient api)
    {
        _api = api; Title = "Shift summary";
        PreviousDayCommand = new AsyncCommand(() => { _day = _day.AddDays(-1); return LoadAsync(); });
        NextDayCommand = new AsyncCommand(() => { if (_day < _today) _day = _day.AddDays(1); return LoadAsync(); });
        RefreshCommand = new AsyncCommand(LoadAsync);
    }
    public ObservableCollection<Option> ShiftOptions { get; } = new();
    public ObservableCollection<SummaryLine> Shops { get; } = new();
    public ObservableCollection<SummaryLine> TopPhenomena { get; } = new();
    public ObservableCollection<SummaryLine> TopMachines { get; } = new();
    public AsyncCommand PreviousDayCommand { get; }
    public AsyncCommand NextDayCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public string DayText => _day == default ? "" : _day.ToString("ddd dd MMM yyyy", CultureInfo.CurrentCulture) + (_day == _today ? " (today)" : "");
    public string TotalText { get => _total; private set => Set(ref _total, value, nameof(TotalText)); }
    public string EntriesText { get => _entries; private set => Set(ref _entries, value, nameof(EntriesText)); }
    public string IncompleteNotice { get => _incomplete; private set { if (Set(ref _incomplete, value, nameof(IncompleteNotice))) OnPropertyChanged(nameof(IsIncomplete)); } }
    public bool IsIncomplete => _incomplete.Length > 0;
    public Option? SelectedShift { get => _shift; set { if (Set(ref _shift, value, nameof(SelectedShift)) && _day != default) _ = LoadAsync(); } }

    public override async Task OnAppearingAsync()
    {
        if (_day != default) { await LoadAsync(); return; }
        await RunBusyAsync(async () =>
        {
            var c = await _api.GetCalendarAsync();
            if (!c.IsSuccess) { ErrorMessage = c.Error!.Message; return; }
            _today = _day = c.Value!.ProductionDate;
            ShiftOptions.Clear(); ShiftOptions.Add(new Option(null, "All shifts"));
            for (var i = 0; i < c.Value.Shifts.Count; i++) ShiftOptions.Add(new Option(i, $"{c.Value.Shifts[i].ShiftName} (from {c.Value.Shifts[i].StartTime})"));
            _shift = ShiftOptions.FirstOrDefault(o => o.Name.StartsWith("Shift " + c.Value.CurrentShift, StringComparison.Ordinal)) ?? ShiftOptions[0];
            OnPropertyChanged(nameof(SelectedShift));
            _shiftCodes = c.Value.Shifts.Select(s => s.ShiftCode).ToList();
        });
        if (_day != default) await LoadAsync();
    }

    private List<string> _shiftCodes = new();

    public Task LoadAsync() => RunBusyAsync(async () =>
    {
        OnPropertyChanged(nameof(DayText));
        var code = _shift?.Id is int i && i < _shiftCodes.Count ? _shiftCodes[i] : null;
        var r = await _api.GetTotalsAsync(_day, _day, code, top: 5);
        if (!r.IsSuccess) { ErrorMessage = r.Error!.Message; return; }
        var d = r.Value!;
        TotalText = d.Total.Quantity.ToString("N0", CultureInfo.CurrentCulture);
        EntriesText = $"rejected parts in {d.Total.Entries} entries";
        IncompleteNotice = d.UnavailableShops.Count == 0 ? "" : $"Incomplete: {string.Join(", ", d.UnavailableShops)} could not be read right now.";
        Fill(Shops, d.Shops.Select(s => new SummaryLine(s.ShopName, $"{s.Entries} entries", s.Quantity.ToString("N0", CultureInfo.CurrentCulture))));
        Fill(TopPhenomena, d.TopPhenomena.Select(p => new SummaryLine(p.PhenomenonName, p.ShopCode, p.Quantity.ToString("N0", CultureInfo.CurrentCulture))));
        Fill(TopMachines, d.TopMachines.Select(m => new SummaryLine(m.MachineCode, $"{m.MachineName} · {m.ShopCode}", m.Quantity.ToString("N0", CultureInfo.CurrentCulture))));
    });

    private static void Fill(ObservableCollection<SummaryLine> target, IEnumerable<SummaryLine> items) { target.Clear(); foreach (var x in items) target.Add(x); }
}

/// <summary>Feature #8: scan a machine label and go straight to the entry form (or the stage list when a shop has several stages).</summary>
public sealed class ScanViewModel : BaseViewModel
{
    private readonly IApiClient _api; private readonly INavigator _nav;
    private int _handling;
    private string _hint = "Point the camera at the QR label on the machine.";
    public ScanViewModel(IApiClient api, INavigator nav) { (_api, _nav) = (api, nav); Title = "Scan machine"; }
    public string Hint { get => _hint; private set => Set(ref _hint, value, nameof(Hint)); }
    public bool IsScanning => _handling == 0;

    public override Task OnAppearingAsync() { Interlocked.Exchange(ref _handling, 0); OnPropertyChanged(nameof(IsScanning)); ErrorMessage = null; return Task.CompletedTask; }

    /// <summary>Called by the camera view for every detected code; only the first one is handled until the page reappears.</summary>
    public async Task OnScannedAsync(string text)
    {
        if (Interlocked.CompareExchange(ref _handling, 1, 0) != 0) return;
        OnPropertyChanged(nameof(IsScanning));
        var resumed = false;
        await RunBusyAsync(async () =>
        {
            var r = await _api.LookupMachineAsync(text);
            if (!r.IsSuccess) { ErrorMessage = r.Error!.Message; Hint = "Try again, or go back and choose the machine from the list."; resumed = true; return; }
            var x = r.Value!;
            if (x.Stages.Count == 1)
                await _nav.GoToAsync(Routes.Entry, new Dictionary<string, object> { ["shop"] = x.Shop, ["stage"] = x.Stages[0], ["machine"] = x.Machine });
            else if (x.Stages.Count > 1)
                await _nav.GoToAsync(Routes.Stages, new Dictionary<string, object> { ["shop"] = x.Shop });
            else { ErrorMessage = "No inspection stage is configured for this shop."; resumed = true; }
        });
        if (resumed) { await Task.Delay(1500); Interlocked.Exchange(ref _handling, 0); OnPropertyChanged(nameof(IsScanning)); }
    }
}
