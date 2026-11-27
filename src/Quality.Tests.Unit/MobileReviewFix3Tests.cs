using System.Net;
using System.Security.Authentication;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;
using Quality.Mobile.Core.ViewModels;

namespace Quality.Tests.Unit;

/// <summary>Third mobile review: re-scan after Back, user editor partial success, Clear after an unknown outcome,
/// history kept after Back, late 401, device clock skew, draft on sign-out, certificate outcome, admin shop race, server path.</summary>
public sealed partial class MobileViewModelTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task Scan_back_does_not_reopen_the_same_label_at_once()
    {
        await SignInAsync();
        var clock = new ManualClock();
        var machine6 = new MachineDto(6, "M-06", "Press 6");
        _http.Respond = r => Task.FromResult(Json(HttpStatusCode.OK,
            new MachineLookupDto(Shop, new[] { Stage }, r.RequestUri!.Query.Contains("M%3A6") ? machine6 : Machine)));
        var vm = new ScanViewModel(_api, _nav, clock);
        await vm.OnAppearingAsync();
        await vm.OnScannedAsync("RJWLQR:M:5");
        Assert.Single(_nav.Calls);

        await vm.OnAppearingAsync();               // Back from the entry form: the label is still in front of the camera
        await vm.OnScannedAsync("RJWLQR:M:5");
        clock.Now += TimeSpan.FromSeconds(2);
        await vm.OnScannedAsync("RJWLQR:M:5");
        Assert.Single(_nav.Calls);
        Assert.True(vm.IsScanning);

        clock.Now += TimeSpan.FromSeconds(2);      // pause over: the same label opens the form again
        await vm.OnScannedAsync("RJWLQR:M:5");
        Assert.Equal(2, _nav.Calls.Count);

        await vm.OnAppearingAsync();
        await vm.OnScannedAsync("RJWLQR:M:6");     // another label is handled at once
        Assert.Equal(3, _nav.Calls.Count);
        Assert.Equal(machine6, (MachineDto)_nav.Calls.Last().Args!["machine"]);
    }

    [Fact]
    public async Task User_editor_retry_after_a_failed_password_reset_does_not_resend_a_stale_update()
    {
        await SignInAsync("Administrator", 1);
        var user = new AdminUserDto(8, "E8", "e8", "Old name", "QualityInspector", new[] { 1 }, new[] { "GDC" }, true, DateTime.UtcNow, "v1");
        var resets = 0;
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath switch
        {
            "/api/shops" => Json(HttpStatusCode.OK, new List<ShopDto> { Shop }),
            "/api/admin/users/8" => Json(HttpStatusCode.OK, user with { DisplayName = "New name", Version = _http.Calls.Last().Body!.Contains("\"v1\"") ? "v2" : "v3" }),
            _ => ++resets < 3 ? Problem(HttpStatusCode.InternalServerError, "boom", "boom") : new HttpResponseMessage(HttpStatusCode.NoContent),
        });
        var vm = new UserEditorViewModel(_api, _nav);
        await vm.OnNavigatedToAsync(new Dictionary<string, object> { ["editUser"] = user });
        vm.DisplayName = "New name"; vm.Password = vm.ConfirmPassword = "new-password-1";
        _http.Calls.Clear();

        await vm.SaveAsync();                      // update saved, password reset failed
        Assert.StartsWith("The user was updated, but the password was not changed", vm.ErrorMessage);
        await vm.SaveAsync();                      // retry: only the password step
        Assert.Equal(new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Post }, _http.Calls.Select(c => c.Method));
        Assert.Contains("\"version\":\"v1\"", _http.Calls[0].Body);

        vm.DisplayName = "Newer name";             // changed again: the update is sent, with the version it returned
        _http.Calls.Clear();
        await vm.SaveAsync();
        Assert.Equal(new[] { HttpMethod.Put, HttpMethod.Post }, _http.Calls.Select(c => c.Method));
        Assert.Contains("\"version\":\"v2\"", _http.Calls[0].Body);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("..", _nav.Calls.Last().Route);
    }

    [Fact]
    public async Task Clear_after_an_unknown_outcome_keeps_the_submission_id()
    {
        var vm = await FormAsync();
        _http.Respond = _ => throw new HttpRequestException("connection reset");
        await vm.SubmitAsync();
        var first = PostedSubmissionId(_http.Calls.Single().Body!);
        vm.Reset();                                // Clear, then the same entry again
        Assert.Equal(first, vm.SubmissionId);
        vm.SelectedPhenomenon = vm.Phenomena.Single(); vm.QuantityText = "3";
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.BadRequest, "quantity_invalid", "Quantity must be a whole number from 1 to 9999."));
        await vm.SubmitAsync();
        Assert.Equal(first, PostedSubmissionId(_http.Calls[1].Body!));

        vm.Reset();                                // the outcome is now known (refused): a new entry gets a new id
        Assert.NotEqual(first, vm.SubmissionId);

        var second = vm.SubmissionId;              // the server's payload-mismatch answer still issues a new id
        vm.SelectedPhenomenon = vm.Phenomena.Single();
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.Conflict, "submission_payload_mismatch", "mismatch"));
        await vm.SubmitAsync();
        Assert.NotEqual(second, vm.SubmissionId);
        Assert.Contains("Check My entries", vm.ErrorMessage);
    }

    [Fact]
    public async Task My_entries_keeps_its_pages_after_Back_and_updates_the_corrected_row()
    {
        await SignInAsync();
        _http.Respond = r => Task.FromResult(r.RequestUri!.Query.Contains("cursor=")
            ? Json(HttpStatusCode.OK, new EntryPage(new[] { Entry(1) }, null, Array.Empty<string>()))
            : Json(HttpStatusCode.OK, new EntryPage(new[] { Entry(3), Entry(2) }, "c1", Array.Empty<string>())));
        var vm = new MyEntriesViewModel(_api, _session, _nav);
        await vm.OnAppearingAsync();
        await vm.LoadMoreAsync();
        Assert.Equal(3, vm.Entries.Count);

        await vm.OpenAsync(vm.Entries[1]);         // entry 2: its detail screen shows it with a pending correction
        var (_, args) = _nav.Calls.Last();
        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, Entry(2, status: "Pending")));
        await new EntryDetailViewModel(_api, _nav, _session, _dialogs).OnNavigatedToAsync(args!);
        _http.Calls.Clear();

        await vm.OnAppearingAsync();               // Back
        Assert.Empty(_http.Calls);
        Assert.Equal(new long[] { 3, 2, 1 }, vm.Entries.Select(e => e.Id));
        Assert.Equal("Correction requested", vm.Entries[1].Badge);

        await vm.OpenAsync(vm.Entries[0]);         // nothing changed this time
        await vm.OnAppearingAsync();
        Assert.Empty(_http.Calls);
        Assert.Equal(3, vm.Entries.Count);
    }

    [Fact]
    public async Task A_late_401_for_an_earlier_session_does_not_end_the_new_one()
    {
        await SignInAsync();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        _http.Respond = _ => gate.Task;
        var call = _api.GetShopsAsync();           // sent with "tok"
        await _session.StartAsync(new LoginResponse("tok2", DateTime.UtcNow.AddHours(8), 42, "E42", "User", "QualityInspector"));
        gate.SetResult(Problem(HttpStatusCode.Unauthorized, "token_expired", "expired"));
        Assert.Equal(ApiErrorKind.SessionExpired, (await call).Error!.Kind);
        Assert.True(_session.IsAuthenticated);
        Assert.Equal("tok2", _session.Token);

        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.Unauthorized, "token_expired", "expired"));
        await _api.GetShopsAsync();                // a 401 for the current token still ends it
        Assert.False(_session.IsAuthenticated);
    }

    [Fact]
    public async Task A_device_clock_hours_ahead_does_not_expire_a_new_session()
    {
        var clock = new ManualClock { Now = DateTimeOffset.UtcNow.AddHours(10) };   // the tablet is 10 h ahead
        var session = new Session(new Store(), clock);
        var api = new ApiClient(new HttpClient(_http), new Settings(), session, clock);
        _http.Respond = r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/login")) return Task.FromResult(Json(HttpStatusCode.OK, new List<ShopDto> { Shop }));
            var m = Json(HttpStatusCode.OK, new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 42, "E42", "User", "QualityInspector"));
            m.Headers.Date = DateTimeOffset.UtcNow;   // server time
            return Task.FromResult(m);
        };
        await new LoginViewModel(api, _nav, session, _drafts) { Username = "E42", Password = "secret-password" }.LoginAsync();
        Assert.True(session.IsAuthenticated);
        Assert.InRange(session.User!.ExpiresAtUtc - clock.Now.UtcDateTime, TimeSpan.FromHours(7.9), TimeSpan.FromHours(8.1));
        Assert.True((await api.GetShopsAsync()).IsSuccess);
        Assert.Contains(_http.Calls, c => c.Path == "/api/shops");
    }

    [Fact]
    public async Task Explicit_sign_out_discards_the_entry_draft()
    {
        await SignInAsync();
        _drafts.Save(new EntryDraft(42, Shop, Stage, Machine, 9, "3", "x", Guid.NewGuid()));
        await new SettingsViewModel(_api, new Settings(), _session, _nav, _dialogs, _drafts).LogoutAsync();
        Assert.False(_session.IsAuthenticated);
        Assert.Null(_drafts.Take(42));

        await SignInAsync();                       // a server change signs out too
        _drafts.Save(new EntryDraft(42, Shop, Stage, Machine, 9, "3", "x", Guid.NewGuid()));
        await new SettingsViewModel(_api, new Settings(), _session, _nav, _dialogs, _drafts) { ServerUrl = "https://other.test" }.SaveAsync();
        Assert.Null(_drafts.Take(42));
    }

    [Fact]
    public async Task Certificate_failure_is_not_reported_as_a_possibly_saved_entry()
    {
        var vm = await FormAsync();
        _http.Respond = _ => throw new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));
        await vm.SubmitAsync();
        Assert.Equal(ApiClient.CertificateMessage, vm.ErrorMessage);   // no "may not have been saved"

        Assert.False(new ApiError(ApiErrorKind.Network, "certificate_untrusted", "x").OutcomeUnknown);
        Assert.False(new ApiError(ApiErrorKind.NotConfigured, "invalid_server_address", "x").OutcomeUnknown);
        Assert.True(new ApiError(ApiErrorKind.Network, "network", "x").OutcomeUnknown);
        Assert.True(new ApiError(ApiErrorKind.Timeout, "timeout", "x").OutcomeUnknown);
    }

    [Fact]
    public async Task Admin_phenomena_slow_stages_of_an_earlier_shop_are_not_added_to_the_newer_shop()
    {
        await SignInAsync("Administrator", 1);
        var shop2 = new ShopDto(2, "CNC", "CNC Shop");
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        _http.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/shops" => Task.FromResult(Json(HttpStatusCode.OK, new List<ShopDto> { Shop, shop2 })),
            "/api/shops/1/stages" => slow.Task,
            "/api/shops/2/stages" => Task.FromResult(Json(HttpStatusCode.OK, new List<StageDto> { new(2, "CNC_FINAL", "CNC Final") })),
            _ => Task.FromResult(Json(HttpStatusCode.OK, new List<AdminPhenomenonDto>())),
        };
        var vm = new AdminPhenomenaViewModel(_api, _nav, _dialogs);
        await vm.OnAppearingAsync();
        vm.SelectedShop = vm.Shops[1];             // GDC: slow
        vm.SelectedShop = vm.Shops[2];             // CNC: answers at once
        await WaitUntilAsync(() => vm.Stages.Count == 2);   // the newer shop is not ignored while the first one loads

        slow.SetResult(Json(HttpStatusCode.OK, new List<StageDto> { Stage }));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "All stages", "CNC Final" }, vm.Stages.Select(s => s.Name));
        Assert.Equal(vm.Shops[2], vm.SelectedShop);
    }

    [Fact]
    public async Task Changing_only_the_server_path_signs_out()
    {
        await SignInAsync();
        var settings = new Settings { BaseUrl = "https://qr.test/prod" };
        var vm = new SettingsViewModel(_api, settings, _session, _nav, _dialogs) { ServerUrl = "https://QR.test/prod/" };
        await vm.SaveAsync();                      // same deployment (host case, trailing slash)
        Assert.True(_session.IsAuthenticated);

        vm.ServerUrl = "https://qr.test/test";     // same host, another deployment
        await vm.SaveAsync();
        Assert.False(_session.IsAuthenticated);
        Assert.Equal("https://qr.test/test", settings.BaseUrl);
        Assert.Equal(Routes.Login, _nav.Calls.Last().Route);
    }
}
