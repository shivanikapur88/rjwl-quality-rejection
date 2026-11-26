using System.Net;
using System.Text.Json;
using Quality.Core.Dtos;
using Quality.Core.Entities;
using Quality.Core.Services;
using Quality.Mobile.Core.ViewModels;

namespace Quality.Tests.Unit;

public sealed class ShiftCalendarTests
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static DateTime Utc(int d, int h, int m) => new DateTime(2026, 11, d, h, m, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(26, 0, 30, "2026-11-26", "A")]   // 06:00 IST: first minute of shift A
    [InlineData(26, 0, 29, "2026-11-25", "C")]   // 05:59 IST: still the previous production day, shift C
    [InlineData(26, 8, 29, "2026-11-26", "A")]   // 13:59 IST
    [InlineData(26, 8, 30, "2026-11-26", "B")]   // 14:00 IST
    [InlineData(26, 16, 30, "2026-11-26", "C")]  // 22:00 IST
    [InlineData(26, 20, 30, "2026-11-26", "C")]  // 02:00 IST on 27 Nov -> night shift of the 26th
    public void Resolves_production_day_and_shift_in_plant_time(int d, int h, int m, string day, string shift)
    {
        var (pd, s) = ShiftCalendar.Default(Ist).Resolve(Utc(d, h, m));
        Assert.Equal((DateOnly.Parse(day), shift), (pd, s));
    }

    [Fact]
    public void Custom_two_shift_pattern_and_validation()
    {
        var cal = new ShiftCalendar(new[]
        {
            new ShiftDefinition { ShiftCode = "D", StartTime = new(8, 0), SortOrder = 1 },
            new ShiftDefinition { ShiftCode = "N", StartTime = new(20, 0), SortOrder = 2 },
            new ShiftDefinition { ShiftCode = "X", StartTime = new(12, 0), SortOrder = 3, IsActive = false },
        }, Ist);
        Assert.Equal((new DateOnly(2026, 11, 25), "N"), cal.Resolve(Utc(26, 1, 0)));   // 06:30 IST
        Assert.Equal((new DateOnly(2026, 11, 26), "D"), cal.Resolve(Utc(26, 7, 0)));   // 12:30 IST (inactive X ignored)
        Assert.Throws<InvalidOperationException>(() => new ShiftCalendar(Array.Empty<ShiftDefinition>(), Ist));
        Assert.True(cal.IsKnownShift("X"));   // retired shift codes stay queryable (review #3)
        Assert.False(cal.IsKnownShift("Z"));
    }
}

public sealed class QrPayloadTests
{
    [Theory]
    [InlineData("RJWLQR:M:12", 12)] [InlineData(" rjwlqr:m:7 ", 7)] [InlineData("RJWLQR:M:12:GDC-02", 12)]
    [InlineData("RJWLQR:M:0", null)] [InlineData("RJWLQR:M:-3", null)] [InlineData("https://evil", null)] [InlineData("RJWLQR:M:", null)] [InlineData(null, null)]
    public void Parses_only_our_labels(string? text, int? id) => Assert.Equal(id, MachineLookupService.ParsePayload(text));

    [Fact]
    public void Label_round_trips()
    {
        Assert.Equal(42, MachineLookupService.ParsePayload(MachineLookupService.LabelPayload(42)));
    }
}

public sealed partial class MobileViewModelTests
{
    private static EntryDto Entry(long id = 7, int inspector = 42, string? status = null, bool reversed = false) =>
        new(id, Guid.NewGuid(), 1, "GDC", 1, "GDC_SHOP", "GDC Shop", 5, "M-05", "Press 5", 9, "Cold Shot", 4, inspector, "E42", "orig", DateTime.UtcNow,
            new DateOnly(2026, 11, 26), "B", reversed, null, status);

    private async Task<EntryDetailViewModel> DetailAsync(EntryDto e)
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 42, "E42", "Inspector", "QualityInspector"));
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/phenomena") => Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen, new(10, "X", "Gate Broken") }),
            var p when p.EndsWith("/machines") => Json(HttpStatusCode.OK, new List<MachineDto> { Machine, new(6, "M-06", "Press 6") }),
            var p when p.EndsWith("/corrections") => Json(HttpStatusCode.Created, new CorrectionDto(1, 1, "GDC", e.Id, null, 42, "E42", DateTime.UtcNow, "r", false, null, null, null, null, 2, null, "Pending", null, null, null, null, null)),
            _ => Json(HttpStatusCode.OK, e),
        });
        var vm = new EntryDetailViewModel(_api, _nav, _session, _dialogs);
        await vm.OnNavigatedToAsync(new Dictionary<string, object> { ["entry"] = e });
        return vm;
    }

    [Fact]
    public async Task Correction_request_sends_only_changed_values()
    {
        var vm = await DetailAsync(Entry());
        Assert.True(vm.CanRequest);
        await vm.OpenFormCommand.ExecuteAsync();
        Assert.Equal((Phen, Machine, "4"), (vm.SelectedPhenomenon, vm.SelectedMachine, vm.QuantityText));

        vm.Reason = ""; Assert.Null(vm.BuildRequest(out var p1)); Assert.Contains("reason", p1);
        vm.Reason = "typed 4"; vm.CorrectValues = true;
        Assert.Null(vm.BuildRequest(out var p2)); Assert.Contains("Nothing was changed", p2);
        vm.QuantityText = "2";
        Assert.Equal(new CorrectionRequest("typed 4", null, null, 2, null), vm.BuildRequest(out _));

        await vm.SubmitAsync();
        var body = JsonDocument.Parse(_http.Calls.Last(c => c.Method == HttpMethod.Post).Body!).RootElement;
        Assert.Equal(2, body.GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("phenomenonId").ValueKind);
        Assert.False(vm.CanRequest);   // now pending
        Assert.Equal("Request sent", _dialogs.Alerts.Last());
    }

    [Fact]
    public async Task Remove_request_has_no_values_and_other_peoples_or_reversed_entries_cannot_be_requested()
    {
        var vm = await DetailAsync(Entry());
        await vm.OpenFormCommand.ExecuteAsync();
        vm.Reason = "entered twice";
        Assert.Equal(new CorrectionRequest("entered twice"), vm.BuildRequest(out _));
        Assert.False((await DetailAsync(Entry(inspector: 99))).CanRequest);
        Assert.False((await DetailAsync(Entry(reversed: true))).CanRequest);
        Assert.False((await DetailAsync(Entry(status: "Pending"))).CanRequest);
        Assert.Equal("Reversed (corrected)", EntryText.Badge(Entry(reversed: true)));
        Assert.Contains("Shift B, 26 Nov", EntryText.When(Entry()));
    }

    [Fact]
    public async Task Supervisor_rejects_with_a_note_and_the_list_refreshes()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 3, "S1", "Sup", "Supervisor"));
        var pending = new CorrectionDto(11, 1, "GDC", 7, Entry(), 42, "E42", DateTime.UtcNow, "typed 4", false, null, null, null, null, 2, null, "Pending", null, null, null, null, null);
        var listed = 0;
        _http.Respond = r => Task.FromResult(r.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, ++listed == 1 ? new List<CorrectionDto> { pending } : new List<CorrectionDto>())
            : Json(HttpStatusCode.OK, pending with { Status = "Rejected" }));
        var vm = new CorrectionsViewModel(_api, _session, _dialogs);
        await vm.OnAppearingAsync();
        var item = Assert.Single(vm.Items);
        Assert.True(item.CanDecide); Assert.False(item.CanWithdraw);
        Assert.Equal("Change to: quantity 2", item.Proposal);

        _dialogs.NextPrompt = "  machine was right  ";
        await vm.RejectCommand.ExecuteAsync(item);
        var post = _http.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.EndsWith("/api/corrections/11/reject", post.Path);
        Assert.Equal("machine was right", JsonDocument.Parse(post.Body!).RootElement.GetProperty("note").GetString());
        Assert.True(vm.IsEmpty);

        _dialogs.NextPrompt = null;   // cancelled: nothing sent
        listed = 0; await vm.LoadAsync(); await vm.RejectCommand.ExecuteAsync(vm.Items[0]);
        Assert.Single(_http.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Summary_loads_today_and_current_shift_and_shows_incomplete_data()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 3, "S1", "Sup", "Supervisor"));
        var day = new DateOnly(2026, 11, 26);
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/calendar")
            ? Json(HttpStatusCode.OK, new CalendarDto(day, "B", "Asia/Kolkata", new[] { new ShiftDto("A", "Shift A", "06:00"), new ShiftDto("B", "Shift B", "14:00"), new ShiftDto("C", "Shift C", "22:00") }))
            : Json(HttpStatusCode.OK, new ReportDto(day, day, "B", new ReportTotal(37, 12), new[] { new ShopTotalDto(1, "GDC", "GDC Shop", 37, 12) },
                Array.Empty<DayShiftTotalDto>(), new[] { new PhenomenonTotalDto(9, "Cold Shot", "GDC", 20, 5) }, new[] { new MachineTotalDto(5, "M-05", "Press 5", "GDC", 25, 7) }, new[] { "CNC" })));
        var vm = new SummaryViewModel(_api);
        await vm.OnAppearingAsync();
        Assert.Equal("Shift B (from 14:00)", vm.SelectedShift!.Name);
        Assert.Contains("fromDate=2026-11-26&toDate=2026-11-26&top=5&shiftCode=B", _http.Calls.Last().Path);
        Assert.Equal(("37", "Cold Shot", "M-05"), (vm.TotalText, vm.TopPhenomena[0].Name, vm.TopMachines[0].Name));
        Assert.True(vm.IsIncomplete);
        await vm.NextDayCommand.ExecuteAsync();   // cannot go past today
        Assert.Contains("fromDate=2026-11-26", _http.Calls.Last().Path);
        await vm.PreviousDayCommand.ExecuteAsync();
        Assert.Contains("fromDate=2026-11-25", _http.Calls.Last().Path);
    }

    [Fact]
    public async Task Summary_ignores_a_slow_response_from_an_earlier_tap()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 3, "S1", "Sup", "Supervisor"));
        var day = new DateOnly(2026, 11, 26);
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        ReportDto Rep(int q) => new(day, day, null, new ReportTotal(q, 1), Array.Empty<ShopTotalDto>(), Array.Empty<DayShiftTotalDto>(), Array.Empty<PhenomenonTotalDto>(), Array.Empty<MachineTotalDto>(), Array.Empty<string>());
        _http.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/calendar")
            ? Task.FromResult(Json(HttpStatusCode.OK, new CalendarDto(day, "A", "Asia/Kolkata", new[] { new ShiftDto("A", "Shift A", "06:00") })))
            : r.RequestUri.Query.Contains("2026-11-25") ? slow.Task                       // day-1: answers late
            : Task.FromResult(Json(HttpStatusCode.OK, Rep(r.RequestUri.Query.Contains("2026-11-24") ? 24 : 26)));
        var vm = new SummaryViewModel(_api);
        await vm.OnAppearingAsync();
        // (The same button cannot run twice at once - AsyncCommand blocks it - but "‹" then "›" can overlap.)
        var first = vm.PreviousDayCommand.ExecuteAsync();    // -> 25 Nov (slow)
        var second = vm.NextDayCommand.ExecuteAsync();       // -> back to 26 Nov, answers at once
        await second;
        slow.SetResult(Json(HttpStatusCode.OK, Rep(25)));
        await first;
        Assert.Equal("26", vm.TotalText);                    // not overwritten by the late 25 Nov answer
        Assert.StartsWith(day.ToString("ddd dd MMM yyyy", System.Globalization.CultureInfo.CurrentCulture), vm.DayText);
    }

    [Fact]
    public async Task Scan_opens_the_entry_form_once_and_recovers_from_a_bad_label()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 42, "E42", "Inspector", "QualityInspector"));
        var vm = new ScanViewModel(_api, _nav);
        _http.Respond = r => Task.FromResult(r.RequestUri!.Query.Contains("RJWLQR")
            ? Json(HttpStatusCode.OK, new MachineLookupDto(Shop, new[] { Stage }, Machine))
            : Problem(HttpStatusCode.BadRequest, "qr_invalid", "This is not a Quality Rejection machine label."));
        await vm.OnScannedAsync("hello");
        Assert.Equal("This is not a Quality Rejection machine label.", vm.ErrorMessage);
        Assert.True(vm.IsScanning);   // scanning resumes
        await Task.WhenAll(vm.OnScannedAsync("RJWLQR:M:5"), vm.OnScannedAsync("RJWLQR:M:5"));   // camera reports the same code twice
        var (route, args) = Assert.Single(_nav.Calls);
        Assert.Equal(("entry", Machine), (route, (MachineDto)args!["machine"]));
    }
}
