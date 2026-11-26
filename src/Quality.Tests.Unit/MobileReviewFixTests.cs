using System.Net;
using System.Security.Authentication;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;
using Quality.Mobile.Core.ViewModels;

namespace Quality.Tests.Unit;

/// <summary>Mobile review fixes: server change signs out, stale summary/history results, command crashes, TLS message,
/// stored http:// address, one-shot draft, pending correction, scanned machine kept through the stage screen.</summary>
public sealed partial class MobileViewModelTests
{
    private Task SignInAsync(string role = "QualityInspector", int id = 42) =>
        _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), id, "E" + id, "User", role));

    [Fact]
    public async Task Changing_the_server_host_signs_out_and_returns_to_sign_in()
    {
        await SignInAsync();
        var settings = new Settings();
        var vm = new SettingsViewModel(_api, settings, _session, _nav, _dialogs) { ServerUrl = "https://qr.test/" };
        await vm.SaveAsync();   // same server: still signed in, stays here
        Assert.True(_session.IsAuthenticated);
        Assert.Empty(_nav.Calls);

        vm.ServerUrl = "https://other.test";
        await vm.SaveAsync();
        Assert.False(_session.IsAuthenticated);
        Assert.Equal("https://other.test", settings.BaseUrl);
        Assert.Equal("Signed out", Assert.Single(_dialogs.Alerts));
        Assert.Equal(Routes.Login, _nav.Calls.Last().Route);
    }

    [Fact]
    public async Task Summary_failed_load_of_another_day_does_not_show_the_old_numbers()
    {
        await SignInAsync("Supervisor", 3);
        var day = new DateOnly(2026, 11, 26);
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/calendar")
            ? Json(HttpStatusCode.OK, new CalendarDto(day, "A", "Asia/Kolkata", new[] { new ShiftDto("A", "Shift A", "06:00") }))
            : r.RequestUri.Query.Contains("2026-11-25") ? Problem(HttpStatusCode.InternalServerError, "boom", "boom")
            : Json(HttpStatusCode.OK, new ReportDto(day, day, "A", new ReportTotal(37, 12), new[] { new ShopTotalDto(1, "GDC", "GDC Shop", 37, 12) },
                Array.Empty<DayShiftTotalDto>(), new[] { new PhenomenonTotalDto(9, "Cold Shot", "GDC", 20, 5) }, new[] { new MachineTotalDto(5, "M-05", "Press 5", "GDC", 25, 7) }, new[] { "CNC" })));
        var vm = new SummaryViewModel(_api);
        await vm.OnAppearingAsync();
        Assert.Equal("37", vm.TotalText);

        await vm.PreviousDayCommand.ExecuteAsync();   // 25 Nov fails
        Assert.StartsWith(day.AddDays(-1).ToString("ddd dd MMM yyyy", System.Globalization.CultureInfo.CurrentCulture), vm.DayText);
        Assert.True(vm.HasError);
        Assert.Equal("–", vm.TotalText);
        Assert.Empty(vm.Shops); Assert.Empty(vm.TopPhenomena); Assert.Empty(vm.TopMachines);
        Assert.False(vm.IsIncomplete);

        await vm.NextDayCommand.ExecuteAsync();       // back to 26 Nov: its numbers again
        Assert.Equal("37", vm.TotalText);
    }

    [Fact]
    public async Task An_exception_in_a_command_does_not_escape_Execute()
    {
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = new AsyncCommand(async () => { await Task.Yield(); throw new InvalidOperationException("navigation failed"); }, onError: e => failed.TrySetResult(e));
        cmd.Execute(null);
        Assert.IsType<InvalidOperationException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(cmd.CanExecute(null));   // usable again

        var failedT = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmdT = new AsyncCommand<string>(_ => throw new InvalidOperationException("sync"), onError: e => failedT.TrySetResult(e));
        cmdT.Execute("x");
        Assert.Equal("sync", (await failedT.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Message);

        new AsyncCommand(() => throw new InvalidOperationException("no listener")).Execute(null);   // no callback: logged, not thrown
    }

    [Fact]
    public async Task Untrusted_certificate_is_not_reported_as_no_connection()
    {
        _http.Respond = _ => throw new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));
        var r = await _api.HealthAsync();
        Assert.Equal(("certificate_untrusted", "The server's security certificate is not trusted on this device. Contact IT."), (r.Error!.Code, r.Error.Message));

        _http.Respond = _ => throw new HttpRequestException("No route to host");
        Assert.StartsWith("Cannot reach the server", (await _api.HealthAsync()).Error!.Message);
    }

    [Fact]
    public async Task A_stored_http_address_is_refused_before_the_token_is_sent()
    {
        await SignInAsync();
        var api = new ApiClient(new HttpClient(_http), new Settings { BaseUrl = "http://qr.test" }, _session);
        var r = await api.GetShopsAsync();
        Assert.Equal((ApiErrorKind.NotConfigured, "invalid_server_address"), (r.Error!.Kind, r.Error.Code));
        Assert.Contains("https://", r.Error.Message);
        Assert.Empty(_http.Calls);
        Assert.True(_session.IsAuthenticated);
    }

    [Fact]
    public async Task Refresh_drops_a_slow_load_more_page_and_owns_the_busy_state()
    {
        await SignInAsync("Supervisor", 3);
        EntryDto E(long id) => new(id, Guid.NewGuid(), 1, "GDC", 1, "GDC_SHOP", "GDC Shop", 5, "M-05", "Press 5", 9, "Cold Shot", 1, 42, "E42", null, DateTime.UtcNow);
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var firstPages = 0;
        _http.Respond = r => r.RequestUri!.Query.Contains("cursor=c1") ? slow.Task
            : Task.FromResult(Json(HttpStatusCode.OK, ++firstPages == 1
                ? new EntryPage(new[] { E(3), E(2) }, "c1", Array.Empty<string>())
                : new EntryPage(new[] { E(4), E(3) }, "c2", Array.Empty<string>())));
        var vm = new MyEntriesViewModel(_api, _session, _nav);
        await vm.LoadAsync();

        var more = vm.LoadMoreAsync();     // slow
        Assert.True(vm.IsBusy);
        await vm.LoadAsync();              // pull to refresh meanwhile
        Assert.False(vm.IsBusy);
        slow.SetResult(Json(HttpStatusCode.OK, new EntryPage(new[] { E(1) }, null, Array.Empty<string>())));
        await more;

        Assert.Equal(new long[] { 4, 3 }, vm.Entries.Select(e => e.Id));   // the old page was not appended
        Assert.True(vm.HasMore);
        Assert.False(vm.IsBusy);
        _http.Calls.Clear();
        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new EntryPage(Array.Empty<EntryDto>(), null, Array.Empty<string>())));
        await vm.LoadMoreAsync();
        Assert.Contains("cursor=c2", _http.Calls.Single().Path);           // the cursor is the refresh's
    }

    [Fact]
    public async Task Entry_draft_is_restored_once_and_only_after_the_phenomena_loaded()
    {
        await SignInAsync();
        var draft = new EntryDraft(42, Shop, Stage, Machine, 9, "3", "edge crack", Guid.NewGuid());
        var args = new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine, ["draft"] = draft };
        var vm = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);

        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.InternalServerError, "boom", "boom"));
        await vm.OnNavigatedToAsync(args);
        Assert.NotEqual(draft.SubmissionId, vm.SubmissionId);   // not restored into an empty form
        Assert.Contains("server had a problem", vm.ErrorMessage);

        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen }));
        await vm.OnNavigatedToAsync(args);                      // retried: now restored
        Assert.Equal((draft.SubmissionId, "3", Phen), (vm.SubmissionId, vm.QuantityText, vm.SelectedPhenomenon));

        vm.Reset();                                             // e.g. saved, then a new entry
        await vm.OnNavigatedToAsync(args);                      // Shell applies the same query again
        Assert.NotEqual(draft.SubmissionId, vm.SubmissionId);
        Assert.Null(vm.SelectedPhenomenon);
    }

    [Fact]
    public async Task Correction_already_pending_closes_the_form_and_shows_the_pending_entry()
    {
        var e = Entry();
        var vm = await DetailAsync(e);
        await vm.OpenFormCommand.ExecuteAsync();
        vm.Reason = "entered twice";
        _http.Respond = r => Task.FromResult(r.Method == HttpMethod.Post
            ? Problem(HttpStatusCode.Conflict, "correction_pending", "A correction request for this entry is already waiting for a decision.")
            : Json(HttpStatusCode.OK, e with { CorrectionStatus = "Pending" }));
        _http.Calls.Clear();
        await vm.SubmitAsync();
        Assert.False(vm.FormOpen);
        Assert.False(vm.CanRequest);
        Assert.Equal("Correction requested", vm.Badge);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, _http.Calls.Select(c => c.Method));
        Assert.Contains("already waiting", vm.ErrorMessage);
    }

    [Fact]
    public async Task Scan_of_a_machine_in_a_shop_with_several_stages_keeps_the_machine()
    {
        await SignInAsync();
        var stage2 = new StageDto(2, "GDC_FINAL", "Final inspection");
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/lookup")
            ? Json(HttpStatusCode.OK, new MachineLookupDto(Shop, new[] { Stage, stage2 }, Machine))
            : Json(HttpStatusCode.OK, new List<StageDto> { Stage, stage2 }));
        await new ScanViewModel(_api, _nav).OnScannedAsync("RJWLQR:M:5");
        var (route, args) = Assert.Single(_nav.Calls);
        Assert.Equal(Routes.Stages, route);

        var stages = new StageViewModel(_api, _nav);
        await stages.OnNavigatedToAsync(args!);
        await stages.SelectStageCommand.ExecuteAsync(stages.Stages[1]);
        var (next, nextArgs) = _nav.Calls.Last();
        Assert.Equal((Routes.Entry, stage2, Machine), (next, (StageDto)nextArgs!["stage"], (MachineDto)nextArgs["machine"]));
    }
}
