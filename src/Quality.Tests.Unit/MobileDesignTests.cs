using System.Net;
using System.Text.Json;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.ViewModels;

namespace Quality.Tests.Unit;

/// <summary>App design round: the view-model behaviour behind the redesigned screens (entry form steps, Clear confirmation,
/// shop name on the confirmation, Reject asking again, shift summary "›" on today, plurals, "Top 5", chip tones).</summary>
public sealed partial class MobileViewModelTests
{
    /// <summary>Dialogs with a chosen Confirm answer and a queue of Prompt answers; records what was asked.</summary>
    private sealed class DesignDialogs : IDialogs
    {
        public bool ConfirmResult { get; set; } = true;
        public readonly List<string> Confirms = new();
        public readonly Queue<string?> Prompts = new();
        public readonly List<string> PromptMessages = new();
        public Task AlertAsync(string t, string m) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string t, string m, string a, string c) { Confirms.Add(t); return Task.FromResult(ConfirmResult); }
        public Task<string?> PromptAsync(string t, string m, string a, string c, string? p = null, int max = 500)
        { PromptMessages.Add(m); return Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null); }
    }

    private async Task<EntryFormViewModel> DesignFormAsync(IDialogs dialogs, StageDto? stage = null)
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 42, "E42", "Inspector", "QualityInspector"));
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/phenomena")
            ? Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen }) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var vm = new EntryFormViewModel(_api, _nav, _session, _drafts, dialogs);
        await vm.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = stage ?? Stage, ["machine"] = Machine });
        return vm;
    }

    [Fact]
    public async Task Entry_form_header_is_one_line_and_names_the_stage_only_when_it_differs_from_the_shop()
    {
        var vm = await DesignFormAsync(new DesignDialogs());
        Assert.Equal("GDC Shop › M-05 – Press 5", vm.ContextText);
        var other = await DesignFormAsync(new DesignDialogs(), new StageDto(2, "GDC_FINAL", "Final check"));
        Assert.Equal("GDC Shop › Final check › M-05 – Press 5", other.ContextText);
    }

    [Fact]
    public async Task Entry_form_shows_the_list_until_a_phenomenon_is_chosen_and_Change_brings_it_back_keeping_the_values()
    {
        var vm = await DesignFormAsync(new DesignDialogs());
        Assert.True(vm.IsChoosingPhenomenon);
        Assert.False(vm.HasSelection);

        vm.SelectedPhenomenon = vm.Phenomena.Single();
        vm.QuantityText = "3";
        Assert.False(vm.IsChoosingPhenomenon);
        Assert.True(vm.HasSelection);

        await vm.ChangePhenomenonCommand.ExecuteAsync();
        Assert.True(vm.IsChoosingPhenomenon);
        Assert.Null(vm.SelectedPhenomenon);
        Assert.Equal("3", vm.QuantityText);   // only the choice is undone
    }

    [Fact]
    public async Task Remarks_box_opens_on_request_or_when_there_are_remarks_and_closes_on_clear()
    {
        var vm = await DesignFormAsync(new DesignDialogs());
        Assert.False(vm.IsRemarksOpen);
        Assert.True(vm.CanAddRemarks);
        await vm.AddRemarksCommand.ExecuteAsync();
        Assert.True(vm.IsRemarksOpen);
        Assert.False(vm.CanAddRemarks);

        vm.Reset();
        Assert.False(vm.IsRemarksOpen);
        vm.Remarks = "edge crack";   // e.g. a restored entry: the box shows its remarks
        Assert.True(vm.IsRemarksOpen);
        Assert.Equal("10/500", vm.RemarksCounter);
    }

    [Fact]
    public async Task Clear_asks_first_when_the_form_has_data_and_keeps_it_when_declined()
    {
        var dialogs = new DesignDialogs { ConfirmResult = false };
        var vm = await DesignFormAsync(dialogs);
        await vm.ResetCommand.ExecuteAsync();   // nothing entered: no question
        Assert.Empty(dialogs.Confirms);

        vm.SelectedPhenomenon = vm.Phenomena.Single();
        vm.QuantityText = "4"; vm.Remarks = "x";
        await vm.ResetCommand.ExecuteAsync();
        Assert.Equal("Clear this entry?", Assert.Single(dialogs.Confirms));
        Assert.Equal((Phen, "4", "x"), (vm.SelectedPhenomenon, vm.QuantityText, vm.Remarks));

        dialogs.ConfirmResult = true;
        await vm.ResetCommand.ExecuteAsync();
        Assert.Null(vm.SelectedPhenomenon);
        Assert.Equal(("1", ""), (vm.QuantityText, vm.Remarks));
        Assert.False(vm.HasFormData);
    }

    [Fact]
    public async Task Confirmation_shows_the_shop_name_passed_by_the_form()
    {
        var vm = await DesignFormAsync(new DesignDialogs());
        vm.SelectedPhenomenon = vm.Phenomena.Single();
        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.Created, new CreateEntryResult(Saved(vm.SubmissionId), false)));
        await vm.SubmitAsync();
        var (route, args) = _nav.Calls.Last();
        Assert.Equal("confirmation", route);
        Assert.Equal("GDC Shop", args!["shopName"]);

        var confirmation = new ConfirmationViewModel(_nav);
        await confirmation.OnNavigatedToAsync(args);
        Assert.Equal("GDC Shop", confirmation.Shop);
        await confirmation.OnNavigatedToAsync(new Dictionary<string, object> { ["entry"] = Saved(Guid.NewGuid()) });
        Assert.Equal("GDC", confirmation.Shop);   // no name passed: the code
    }

    private async Task<(CorrectionsViewModel Vm, CorrectionItem Item)> PendingCorrectionAsync(IDialogs dialogs)
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 3, "S1", "Sup", "Supervisor"));
        var pending = new CorrectionDto(11, 1, "GDC", 7, Entry(), 42, "E42", DateTime.UtcNow, "typed 4", false, null, null, null, null, 2, null, "Pending", null, null, null, null, null);
        _http.Respond = r => Task.FromResult(r.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, new List<CorrectionDto> { pending })
            : Json(HttpStatusCode.OK, pending with { Status = "Rejected" }));
        var vm = new CorrectionsViewModel(_api, _session, dialogs);
        await vm.OnAppearingAsync();
        return (vm, Assert.Single(vm.Items));
    }

    [Fact]
    public async Task Reject_with_an_empty_reason_asks_again_with_an_explanation()
    {
        var dialogs = new DesignDialogs();
        dialogs.Prompts.Enqueue("   ");
        dialogs.Prompts.Enqueue("machine was right");
        var (vm, item) = await PendingCorrectionAsync(dialogs);
        await vm.RejectCommand.ExecuteAsync(item);
        Assert.Equal(2, dialogs.PromptMessages.Count);
        Assert.StartsWith("A reason is required", dialogs.PromptMessages[1]);
        var post = _http.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("machine was right", JsonDocument.Parse(post.Body!).RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Reject_stops_on_cancel_or_after_repeated_empty_reasons_without_sending()
    {
        var dialogs = new DesignDialogs();
        dialogs.Prompts.Enqueue("");
        dialogs.Prompts.Enqueue(null);   // cancelled the second time
        var (vm, item) = await PendingCorrectionAsync(dialogs);
        await vm.RejectCommand.ExecuteAsync(item);
        Assert.DoesNotContain(_http.Calls, c => c.Method == HttpMethod.Post);
        Assert.False(vm.HasError);

        dialogs.PromptMessages.Clear();
        for (var i = 0; i < CorrectionsViewModel.MaxRejectPrompts; i++) dialogs.Prompts.Enqueue(" ");
        await vm.RejectCommand.ExecuteAsync(item);
        Assert.Equal(CorrectionsViewModel.MaxRejectPrompts, dialogs.PromptMessages.Count);
        Assert.DoesNotContain(_http.Calls, c => c.Method == HttpMethod.Post);
        Assert.Equal("Give a reason for rejecting the request.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Summary_next_day_is_disabled_on_today_and_texts_are_pluralised_and_cut_lists_say_top_5()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 3, "S1", "Sup", "Supervisor"));
        var day = new DateOnly(2026, 11, 26);
        var phenomena = Enumerable.Range(1, 5).Select(i => new PhenomenonTotalDto(i, $"P{i}", "GDC", 10 - i, 1)).ToArray();
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/calendar")
            ? Json(HttpStatusCode.OK, new CalendarDto(day, "A", "Asia/Kolkata", new[] { new ShiftDto("A", "Shift A", "06:00") }))
            : Json(HttpStatusCode.OK, new ReportDto(day, day, "A", new ReportTotal(1, 1), new[] { new ShopTotalDto(1, "GDC", "GDC Shop", 1, 1) },
                Array.Empty<DayShiftTotalDto>(), phenomena, new[] { new MachineTotalDto(5, "M-05", "Press 5", "GDC", 1, 1) }, Array.Empty<string>())));
        var vm = new SummaryViewModel(_api);
        Assert.False(vm.NextDayCommand.CanExecute(null));   // no calendar yet
        var changes = 0;
        vm.NextDayCommand.CanExecuteChanged += (_, _) => changes++;
        await vm.OnAppearingAsync();

        Assert.False(vm.CanGoNext);
        Assert.False(vm.NextDayCommand.CanExecute(null));   // today: "›" disabled
        Assert.Equal("rejected part in 1 entry", vm.EntriesText);
        Assert.Equal("1 entry", vm.Shops[0].Detail);
        Assert.Equal("Top 5 phenomena", vm.TopPhenomenaHeading);   // as long as asked for: may be cut
        Assert.Equal("Machines", vm.TopMachinesHeading);            // shorter: complete
        Assert.True(vm.HasTopNote);
        Assert.Equal(("12 entries", "1 entry"), (SummaryViewModel.Entries(12), SummaryViewModel.Entries(1)));

        changes = 0;
        await vm.PreviousDayCommand.ExecuteAsync();
        Assert.True(changes > 0);
        Assert.True(vm.NextDayCommand.CanExecute(null));
        await vm.NextDayCommand.ExecuteAsync();
        Assert.False(vm.NextDayCommand.CanExecute(null));
        Assert.Contains("fromDate=2026-11-26", _http.Calls.Last().Path);
    }

    [Theory]
    [InlineData("Correction requested", ChipTone.Warning)]
    [InlineData("Pending", ChipTone.Warning)]
    [InlineData("Reversed (corrected)", ChipTone.Muted)]
    [InlineData("Inactive", ChipTone.Muted)]
    [InlineData("Withdrawn", ChipTone.Muted)]
    [InlineData("Rejected", ChipTone.Danger)]
    [InlineData("Active", ChipTone.Success)]
    [InlineData("Approved", ChipTone.Success)]
    [InlineData("Correction of entry #12", ChipTone.Info)]
    [InlineData(null, ChipTone.Info)]
    public void Status_chips_use_one_tone_per_status(string? text, ChipTone tone) => Assert.Equal(tone, StatusTone.For(text));
}
