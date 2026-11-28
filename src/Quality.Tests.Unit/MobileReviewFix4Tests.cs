using System.Net;
using System.Security.Authentication;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;
using Quality.Mobile.Core.ViewModels;

namespace Quality.Tests.Unit;

/// <summary>Fourth mobile review: RefreshView flag vs "Load more", phenomena retry with the draft kept, unknown outcome kept
/// across leaving the form, certificate detection, unrelated answers, editor stage race, expired token on Home, Test connection.</summary>
public sealed partial class MobileViewModelTests
{
    /// <summary>Named like Android's java.security.cert exception (the detection also looks at the exception type's name).</summary>
    private sealed class CertPathValidatorException(string message) : Exception(message);

    /// <summary>What MAUI's RefreshView does: it runs its command whenever IsRefreshing becomes true (from the user or from code).</summary>
    private static List<bool> AttachRefreshView(BaseViewModel vm, AsyncCommand refresh, Action? onRefresh = null)
    {
        var flags = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BaseViewModel.IsRefreshing)) return;
            flags.Add(vm.IsRefreshing);
            if (vm.IsRefreshing) { onRefresh?.Invoke(); refresh.Execute(null); }
        };
        return flags;
    }

    private Func<HttpRequestMessage, Task<HttpResponseMessage>> PhenomenaOr(Func<HttpRequestMessage, Task<HttpResponseMessage>> other) =>
        r => r.RequestUri!.AbsolutePath.EndsWith("/phenomena") ? Task.FromResult(Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen })) : other(r);

    private static EntryDto Saved(Guid id) => new(1, id, 1, "GDC", 1, "GDC_SHOP", "GDC Shop", 5, "M-05", "Press 5", 9, "Cold Shot", 3, 42, "E42", "edge crack", DateTime.UtcNow);

    [Fact]
    public async Task Load_more_does_not_set_the_refresh_flag_and_a_pull_refreshes_once()
    {
        await SignInAsync("Supervisor", 3);
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        _http.Respond = r => r.RequestUri!.Query.Contains("cursor=c1") ? slow.Task
            : Task.FromResult(Json(HttpStatusCode.OK, new EntryPage(new[] { Entry(3), Entry(2) }, "c1", Array.Empty<string>())));
        var vm = new MyEntriesViewModel(_api, _session, _nav);
        var refreshes = 0;
        var flags = AttachRefreshView(vm, vm.RefreshCommand, () => refreshes++);
        await vm.OnAppearingAsync();               // first load: not a refresh either
        _http.Calls.Clear();

        var more = vm.LoadMoreAsync();
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsLoadingMore);             // its own progress indicator
        Assert.False(vm.IsRefreshing);
        slow.SetResult(Json(HttpStatusCode.OK, new EntryPage(new[] { Entry(1) }, null, Array.Empty<string>())));
        await more;
        Assert.False(vm.IsLoadingMore);
        Assert.Empty(flags);
        Assert.Equal(0, refreshes);
        Assert.Contains("cursor=c1", Assert.Single(_http.Calls).Path);
        Assert.Equal(new long[] { 3, 2, 1 }, vm.Entries.Select(e => e.Id));   // page 2 was not replaced by page 1

        _http.Calls.Clear();
        vm.IsRefreshing = true;                    // the user pulls down
        await WaitUntilAsync(() => !vm.IsRefreshing);
        Assert.Equal(1, refreshes);
        Assert.DoesNotContain("cursor=", Assert.Single(_http.Calls).Path);
        Assert.Equal(new[] { true, false }, flags);
        Assert.Equal(new long[] { 3, 2 }, vm.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Summary_and_corrections_loads_from_code_do_not_set_the_refresh_flag()
    {
        await SignInAsync("Supervisor", 3);
        var day = new DateOnly(2026, 11, 26);
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath switch
        {
            "/api/reports/calendar" => Json(HttpStatusCode.OK, new CalendarDto(day, "A", "Asia/Kolkata", new[] { new ShiftDto("A", "Shift A", "06:00") })),
            "/api/corrections" => Json(HttpStatusCode.OK, new List<CorrectionDto>()),
            _ => Json(HttpStatusCode.OK, new ReportDto(day, day, "A", new ReportTotal(1, 1), Array.Empty<ShopTotalDto>(), Array.Empty<DayShiftTotalDto>(),
                Array.Empty<PhenomenonTotalDto>(), Array.Empty<MachineTotalDto>(), Array.Empty<string>())),
        });
        var summary = new SummaryViewModel(_api);
        var summaryFlags = AttachRefreshView(summary, summary.RefreshCommand);
        await summary.OnAppearingAsync();
        await summary.PreviousDayCommand.ExecuteAsync();
        Assert.Empty(summaryFlags);

        var corrections = new CorrectionsViewModel(_api, _session, _dialogs);
        var correctionFlags = AttachRefreshView(corrections, corrections.RefreshCommand);
        await corrections.OnAppearingAsync();
        corrections.ShowDecided = true;
        await WaitUntilAsync(() => !corrections.IsBusy);
        Assert.Empty(correctionFlags);

        _http.Calls.Clear();
        await corrections.RefreshCommand.ExecuteAsync();   // the pull path sets and always clears the flag
        Assert.Equal(new[] { true, false }, correctionFlags);
        Assert.Single(_http.Calls);
    }

    [Fact]
    public async Task Failed_phenomena_load_can_be_retried_and_the_draft_is_kept_until_restored()
    {
        await SignInAsync();
        var draft = new EntryDraft(42, Shop, Stage, Machine, 9, "3", "edge crack", Guid.NewGuid());
        _drafts.Save(draft);
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/login")
            ? Json(HttpStatusCode.OK, new LoginResponse("tok2", DateTime.UtcNow.AddHours(8), 42, "E42", "Inspector", "QualityInspector"))
            : Problem(HttpStatusCode.InternalServerError, "boom", "boom"));
        await new LoginViewModel(_api, _nav, _session, _drafts) { Username = "E42", Password = "secret-password" }.LoginAsync();
        var (route, args) = _nav.Calls.Last();
        Assert.Equal("entry", route);

        var vm = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await vm.OnNavigatedToAsync(args!);        // phenomena fail
        Assert.True(vm.CanRetryLoad);
        await vm.RetryCommand.ExecuteAsync();      // still failing
        Assert.True(vm.CanRetryLoad);
        Assert.Equal(draft, _drafts.Peek(42));     // kept: not restored yet

        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen }));
        // What the Save button shows is what it last read when CanExecuteChanged was raised (it does not poll).
        bool? saveButtonEnabled = null;
        vm.SubmitCommand.CanExecuteChanged += (_, _) => saveButtonEnabled = vm.SubmitCommand.CanExecute(null);
        await vm.RetryCommand.ExecuteAsync();
        Assert.False(vm.CanRetryLoad);
        Assert.True(saveButtonEnabled, "the Save button was never told it can run again after the restore");
        Assert.Equal((draft.SubmissionId, "3", "edge crack", Phen), (vm.SubmissionId, vm.QuantityText, vm.Remarks, vm.SelectedPhenomenon));
        // Fourth Sonnet review (high): Save must be usable right after the restore (it was left disabled).
        Assert.True(vm.CanSubmit);
        Assert.True(vm.SubmitCommand.CanExecute(null));
        Assert.Null(_drafts.Peek(42));             // restored: removed from the store
    }

    [Fact]
    public async Task A_draft_not_yet_restored_is_resumed_when_the_same_form_is_opened_again()
    {
        await SignInAsync();
        var draft = new EntryDraft(42, Shop, Stage, Machine, 9, "3", "edge crack", Guid.NewGuid());
        _drafts.Save(draft);
        var args = new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine };
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.InternalServerError, "boom", "boom"));
        await new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs).OnNavigatedToAsync(new Dictionary<string, object>(args) { ["draft"] = draft });

        // Back, then the same shop / stage / machine again (no draft argument this time).
        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen }));
        var other = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await other.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = new MachineDto(6, "M-06", "Press 6") });
        Assert.NotEqual(draft.SubmissionId, other.SubmissionId);   // another machine: not this draft
        var vm = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await vm.OnNavigatedToAsync(args);
        Assert.Equal((draft.SubmissionId, Phen), (vm.SubmissionId, vm.SelectedPhenomenon));
        Assert.Null(_drafts.Peek(42));
    }

    [Fact]
    public async Task An_unknown_outcome_survives_leaving_the_form_and_its_id_is_reused_for_the_same_selection()
    {
        var vm = await FormAsync();
        _http.Respond = PhenomenaOr(_ => throw new TaskCanceledException("timeout"));
        await vm.SubmitAsync();
        var first = PostedSubmissionId(_http.Calls.Single().Body!);

        // Back, re-enter the same shop / stage / machine: a new (transient) form, the same submission id.
        var again = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await again.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine });
        Assert.Equal(first, again.SubmissionId);
        again.Reset();                             // Clear keeps it too
        Assert.Equal(first, again.SubmissionId);

        var otherMachine = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await otherMachine.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = new MachineDto(6, "M-06", "Press 6") });
        Assert.NotEqual(first, otherMachine.SubmissionId);

        again.SelectedPhenomenon = again.Phenomena.Single(); again.QuantityText = "3"; again.Remarks = "edge crack";
        _http.Calls.Clear();
        _http.Respond = PhenomenaOr(_ => Task.FromResult(Json(HttpStatusCode.OK, new CreateEntryResult(Saved(first), true))));
        await again.SubmitAsync();                 // the server replays the saved entry
        Assert.Equal(first, PostedSubmissionId(_http.Calls.Single().Body!));
        Assert.True((bool)_nav.Calls.Last().Args!["already"]);

        var next = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);   // settled: a new entry gets a new id
        await next.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine });
        Assert.NotEqual(first, next.SubmissionId);
    }

    [Fact]
    public async Task A_reused_id_with_other_details_gets_a_new_id_from_the_payload_mismatch_answer()
    {
        var vm = await FormAsync();
        _http.Respond = PhenomenaOr(_ => throw new HttpRequestException("connection reset"));
        await vm.SubmitAsync();
        var first = vm.SubmissionId;

        var again = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await again.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine });
        again.SelectedPhenomenon = again.Phenomena.Single(); again.QuantityText = "7";
        _http.Respond = PhenomenaOr(_ => Task.FromResult(Problem(HttpStatusCode.Conflict, "submission_payload_mismatch", "mismatch")));
        await again.SubmitAsync();
        Assert.NotEqual(first, again.SubmissionId);
        Assert.Contains("Check My entries", again.ErrorMessage);
        Assert.Null(_drafts.PendingFor(42, Shop.Id, Stage.Id, Machine.Id));
    }

    [Fact]
    public async Task Answers_not_from_the_entry_endpoint_do_not_settle_an_unknown_outcome()
    {
        var vm = await FormAsync();
        _http.Respond = _ => throw new HttpRequestException("connection reset");
        await vm.SubmitAsync();
        var first = vm.SubmissionId;

        var unrelated = new Func<HttpResponseMessage>[]
        {
            () => Problem(HttpStatusCode.TooManyRequests, "rate_limited", "slow down"),
            () => Problem(HttpStatusCode.Forbidden, "shop_not_assigned", "not yours"),
            () => new HttpResponseMessage(HttpStatusCode.BadRequest),   // a proxy's 400, no API problem code
        };
        foreach (var answer in unrelated)
        {
            _http.Respond = _ => Task.FromResult(answer());
            await vm.SubmitAsync();
            vm.Reset();
            Assert.Equal(first, vm.SubmissionId);
            vm.SelectedPhenomenon = vm.Phenomena.Single(); vm.QuantityText = "3";
        }
        _http.Respond = _ => throw new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));
        await vm.SubmitAsync();
        vm.Reset();
        Assert.Equal(first, vm.SubmissionId);
        Assert.Equal(first, _drafts.PendingFor(42, Shop.Id, Stage.Id, Machine.Id));

        vm.SelectedPhenomenon = vm.Phenomena.Single(); vm.QuantityText = "3";
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.Unauthorized, "token_expired", "expired"));
        await vm.SubmitAsync();                    // session expired: the draft keeps the same id, still pending
        Assert.Equal(first, _drafts.Take(42)!.SubmissionId);
        Assert.Equal(first, _drafts.PendingFor(42, Shop.Id, Stage.Id, Machine.Id));

        await SignInAsync();
        vm.SelectedPhenomenon = vm.Phenomena.Single(); vm.QuantityText = "3";
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.BadRequest, "quantity_invalid", "Quantity must be a whole number from 1 to 9999."));
        await vm.SubmitAsync();                    // the entry endpoint's own refusal settles it
        vm.Reset();
        Assert.NotEqual(first, vm.SubmissionId);
        Assert.Null(_drafts.PendingFor(42, Shop.Id, Stage.Id, Machine.Id));
    }

    [Fact]
    public async Task Certificate_trust_failures_are_recognised_in_any_exception_type()
    {
        var trustFailures = new Exception[]
        {
            new WebException("Error: TrustFailure", new IOException("handshake failed", new CertPathValidatorException("Trust anchor for certification path not found."))),
            new IOException("javax.net.ssl.SSLHandshakeException: java.security.cert.CertPathValidatorException: Trust anchor for certification path not found."),
            new HttpRequestException("The SSL connection could not be established, see inner exception.", new AuthenticationException("Authentication failed")),
            new InvalidOperationException("wrapped", new IOException("Hostname qr.test not verified: certificate: sha256/abc")),
        };
        foreach (var ex in trustFailures)
        {
            _http.Respond = _ => throw ex;
            var r = await _api.HealthAsync();
            Assert.Equal(("certificate_untrusted", ApiClient.CertificateMessage), (r.Error!.Code, r.Error.Message));
            Assert.False(r.Error.OutcomeUnknown);
        }
    }

    [Fact]
    public async Task Handshake_resets_and_timeouts_stay_network_errors()
    {
        var transient = new Exception[]
        {
            new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.",
                new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")),
            new IOException("SSL handshake aborted: ssl=0x7a3c: I/O error during system call, Connection reset by peer"),
            new WebException("SSL handshake timed out", new IOException("Read timed out")),
            new HttpRequestException("No route to host"),
        };
        foreach (var ex in transient)
        {
            _http.Respond = _ => throw ex;
            var r = await _api.HealthAsync();
            Assert.Equal((ApiErrorKind.Network, "network"), (r.Error!.Kind, r.Error.Code));
            Assert.StartsWith("Cannot reach the server", r.Error.Message);
            Assert.True(r.Error.OutcomeUnknown);
        }
    }

    [Fact]
    public async Task Phenomenon_editor_quick_shop_changes_show_only_the_latest_shops_stages()
    {
        await SignInAsync("Administrator", 1);
        var shop2 = new ShopDto(2, "CNC", "CNC Shop");
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        _http.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/shops" => Task.FromResult(Json(HttpStatusCode.OK, new List<ShopDto> { Shop, shop2 })),
            "/api/shops/1/stages" => slow.Task,
            _ => Task.FromResult(Json(HttpStatusCode.OK, new List<StageDto> { new(2, "CNC_FINAL", "CNC Final") })),
        };
        var vm = new PhenomenonEditorViewModel(_api, _nav);
        await vm.OnNavigatedToAsync(new Dictionary<string, object>());
        vm.SelectedShop = vm.Shops[0];             // GDC: slow
        vm.SelectedShop = vm.Shops[1];             // CNC: answers at once
        await WaitUntilAsync(() => vm.Stages.Count == 1);

        slow.SetResult(Json(HttpStatusCode.OK, new List<StageDto> { Stage }));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "CNC Final" }, vm.Stages.Select(s => s.Name));
        Assert.Equal("CNC Final", vm.SelectedStage?.Name);
    }

    [Fact]
    public async Task Home_returns_to_sign_in_when_the_token_ran_out_while_the_app_was_open()
    {
        var clock = new ManualClock();
        var session = new Session(new Store(), clock);
        var expired = 0;
        session.Expired += () => expired++;
        await session.StartAsync(new LoginResponse("tok", clock.Now.UtcDateTime.AddMinutes(30), 42, "E42", "Inspector", "QualityInspector"));
        var api = new ApiClient(new HttpClient(_http), new Settings(), session, clock);
        var vm = new HomeViewModel(api, _nav, session);
        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new List<ShopDto> { Shop }));
        await vm.OnAppearingAsync();               // valid: shops are loaded
        Assert.Equal(0, expired);
        Assert.Single(vm.Shops);

        clock.Now += TimeSpan.FromHours(1);        // the token expired locally; no request has failed yet
        _http.Calls.Clear();
        await vm.OnAppearingAsync();
        Assert.Equal(1, expired);                  // the expiry path (sign-in) runs
        Assert.Null(session.User);
        Assert.Empty(_http.Calls);
    }

    [Fact]
    public async Task Test_connection_checks_the_typed_address_without_saving_it_or_signing_out()
    {
        await SignInAsync();
        var settings = new Settings();
        var hosts = new List<string>();
        _http.Respond = r => { hosts.Add(r.RequestUri!.Host); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); };
        var vm = new SettingsViewModel(_api, settings, _session, _nav, _dialogs, _drafts) { ServerUrl = "https://other.test/" };
        await vm.TestAsync();
        Assert.Equal(new[] { "other.test" }, hosts);
        Assert.Equal("/api/health", Assert.Single(_http.Calls).Path);
        Assert.Equal("https://qr.test", settings.BaseUrl);        // not saved
        Assert.True(_session.IsAuthenticated);                    // not signed out
        Assert.Empty(_nav.Calls); Assert.Empty(_dialogs.Alerts);
        Assert.Contains("Tap Save", vm.InfoMessage);

        _http.Respond = _ => throw new HttpRequestException("No route to host");
        await vm.TestAsync();                                     // a failed test changes nothing either
        Assert.StartsWith("Cannot reach the server", vm.ErrorMessage);
        Assert.Equal("https://qr.test", settings.BaseUrl);
        Assert.True(_session.IsAuthenticated);

        vm.ServerUrl = "http://other.test";
        _http.Calls.Clear();
        await vm.TestAsync();                                     // not a valid address: nothing is sent
        Assert.True(vm.HasError);
        Assert.Empty(_http.Calls);

        vm.ServerUrl = "https://other.test";
        await vm.SaveAsync();                                     // only Save persists, and signs out (another server)
        Assert.Equal("https://other.test", settings.BaseUrl);
        Assert.False(_session.IsAuthenticated);
        Assert.Equal(Routes.Login, _nav.Calls.Last().Route);
    }
}
