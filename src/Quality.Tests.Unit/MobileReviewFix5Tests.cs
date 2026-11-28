using System.Net;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Services;
using Quality.Mobile.Core.ViewModels;

namespace Quality.Tests.Unit;

/// <summary>Fifth full review (Sonnet 5.5): unknown-outcome saves shown again with their details, kept per machine and
/// expiring; the shift summary recovering from a failed calendar load and following "today" on a pull.</summary>
public sealed partial class MobileViewModelTests
{
    private async Task<(Guid Id, EntryFormViewModel Again)> UnknownOutcomeThenReopenAsync()
    {
        var vm = await FormAsync();
        vm.SelectedPhenomenon = vm.Phenomena.Single(); vm.QuantityText = "3"; vm.Remarks = "edge crack";
        _http.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/phenomena")
            ? Task.FromResult(Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen }))
            : throw new TaskCanceledException("timeout");
        await vm.SubmitAsync();
        var id = vm.SubmissionId;
        var again = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await again.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine });
        return (id, again);
    }

    [Fact]
    public async Task A_reopened_form_shows_the_unanswered_entry_again_instead_of_a_blank_form_tied_to_its_id()
    {
        var (id, again) = await UnknownOutcomeThenReopenAsync();
        Assert.Equal(id, again.SubmissionId);
        Assert.Equal(Phen.Id, again.SelectedPhenomenon?.Id);   // the details the id belongs to are on screen
        Assert.Equal("3", again.QuantityText);
        Assert.Equal("edge crack", again.Remarks);
        Assert.Contains("got no answer", again.ErrorMessage);
        Assert.Contains("NEW rejection", again.ErrorMessage);
    }

    [Fact]
    public async Task An_unanswered_save_expires_so_a_later_identical_rejection_gets_its_own_id()
    {
        var now = DateTimeOffset.UtcNow;
        _drafts.Now = () => now;
        var (id, _) = await UnknownOutcomeThenReopenAsync();

        now += EntryDraftStore.PendingLifetime + TimeSpan.FromMinutes(1);   // an hour later, the same defect again
        var later = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await later.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine });
        Assert.NotEqual(id, later.SubmissionId);
        Assert.Null(later.SelectedPhenomenon);
        Assert.Null(_drafts.PendingFor(42, Shop.Id, Stage.Id, Machine.Id));
    }

    [Fact]
    public async Task Unanswered_saves_on_two_machines_both_keep_their_ids()
    {
        var (first, _) = await UnknownOutcomeThenReopenAsync();
        var m6 = new MachineDto(6, "M-06", "Press 6");
        var other = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await other.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = m6 });
        other.SelectedPhenomenon = other.Phenomena.Single(); other.QuantityText = "2";
        await other.SubmitAsync();   // still timing out
        Assert.Equal(first, _drafts.PendingFor(42, Shop.Id, Stage.Id, Machine.Id));   // not overwritten by machine 6
        Assert.Equal(other.SubmissionId, _drafts.PendingFor(42, Shop.Id, Stage.Id, m6.Id));
    }

    [Fact]
    public async Task Summary_recovers_from_a_failed_calendar_load_on_a_pull_and_previous_day_waits_for_it()
    {
        await SignInAsync("Supervisor", 3);
        var day = new DateOnly(2026, 11, 26);
        var calendarUp = false;
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath switch
        {
            "/api/reports/calendar" => calendarUp
                ? Json(HttpStatusCode.OK, new CalendarDto(day, "A", "Asia/Kolkata", new[] { new ShiftDto("A", "Shift A", "06:00") }))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Json(HttpStatusCode.OK, new ReportDto(day, day, "A", new ReportTotal(4, 2), Array.Empty<ShopTotalDto>(), Array.Empty<DayShiftTotalDto>(),
                Array.Empty<PhenomenonTotalDto>(), Array.Empty<MachineTotalDto>(), Array.Empty<string>())),
        });
        var summary = new SummaryViewModel(_api);
        await summary.OnAppearingAsync();
        Assert.NotNull(summary.ErrorMessage);
        Assert.False(summary.PreviousDayCommand.CanExecute(null));   // no day to step back from

        calendarUp = true;
        _http.Calls.Clear();
        await summary.RefreshCommand.ExecuteAsync();
        Assert.Contains(_http.Calls, c => c.Path.Contains("/api/reports/calendar"));
        Assert.Equal("4", summary.TotalText);
        Assert.Contains("(today)", summary.DayText);
        Assert.True(summary.PreviousDayCommand.CanExecute(null));
    }

    [Fact]
    public async Task Summary_left_open_on_today_moves_to_the_new_production_day_and_shift_on_a_pull()
    {
        await SignInAsync("Supervisor", 3);
        var day = new DateOnly(2026, 11, 26); var shift = "A";
        var shifts = new[] { new ShiftDto("A", "Shift A", "06:00"), new ShiftDto("B", "Shift B", "14:00"), new ShiftDto("C", "Shift C", "22:00") };
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath == "/api/reports/calendar"
            ? Json(HttpStatusCode.OK, new CalendarDto(day, shift, "Asia/Kolkata", shifts))
            : Json(HttpStatusCode.OK, new ReportDto(day, day, shift, new ReportTotal(0, 0), Array.Empty<ShopTotalDto>(), Array.Empty<DayShiftTotalDto>(),
                Array.Empty<PhenomenonTotalDto>(), Array.Empty<MachineTotalDto>(), Array.Empty<string>())));
        var summary = new SummaryViewModel(_api);
        await summary.OnAppearingAsync();
        Assert.Equal("Shift A (from 06:00)", summary.SelectedShift!.Name);

        day = day.AddDays(1); shift = "B";   // the screen stayed open past midnight and into shift B
        await summary.RefreshCommand.ExecuteAsync();
        Assert.Contains("27", summary.DayText);
        Assert.Contains("(today)", summary.DayText);
        Assert.Equal("Shift B (from 14:00)", summary.SelectedShift!.Name);
    }
}
