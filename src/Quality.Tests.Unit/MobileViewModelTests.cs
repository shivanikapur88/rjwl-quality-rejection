using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;
using Quality.Mobile.Core.ViewModels;

namespace Quality.Tests.Unit;

/// <summary>
/// The recovered mobile logic, exercised through the REAL ApiClient and Session with a scripted HTTP handler
/// (no device, no MAUI). Covers brief §7 / §14: double taps, uncertain outcomes, session expiry, inactive items, paging.
/// </summary>
public sealed class MobileViewModelTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string? Body)> Calls = new();
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.Method, r.RequestUri!.PathAndQuery, r.Content is null ? null : await r.Content.ReadAsStringAsync(ct)));
            return await Respond(r);
        }
    }
    private sealed class Settings : IAppSettings { public string BaseUrl { get; set; } = "https://qr.test"; }
    private sealed class Store : ISecureStore
    {
        private readonly Dictionary<string, string> _d = new();
        public Task<string?> GetAsync(string k) => Task.FromResult(_d.TryGetValue(k, out var v) ? v : null);
        public Task SetAsync(string k, string v) { _d[k] = v; return Task.CompletedTask; }
        public void Remove(string k) => _d.Remove(k);
    }
    private sealed class Nav : INavigator
    {
        public readonly List<(string Route, IDictionary<string, object>? Args)> Calls = new();
        public Task GoToAsync(string route, IDictionary<string, object>? args = null) { Calls.Add((route, args)); return Task.CompletedTask; }
        public Task GoBackAsync() { Calls.Add(("..", null)); return Task.CompletedTask; }
    }
    private sealed class Dialogs : IDialogs
    {
        public readonly List<string> Alerts = new();
        public Task AlertAsync(string t, string m) { Alerts.Add(t); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string t, string m, string a, string c) => Task.FromResult(true);
    }

    private static readonly ShopDto Shop = new(1, "GDC", "GDC Shop");
    private static readonly StageDto Stage = new(1, "GDC_SHOP", "GDC Shop");
    private static readonly MachineDto Machine = new(5, "M-05", "Press 5");
    private static readonly PhenomenonDto Phen = new(9, "GDC_SHOP-009", "Cold Shot");

    private readonly Handler _http = new();
    private readonly Nav _nav = new();
    private readonly Dialogs _dialogs = new();
    private readonly Session _session = new(new Store(), TimeProvider.System);
    private readonly EntryDraftStore _drafts = new();
    private readonly ApiClient _api;

    public MobileViewModelTests() => _api = new ApiClient(new HttpClient(_http), new Settings(), _session);

    private static HttpResponseMessage Json<T>(HttpStatusCode s, T body) => new(s) { Content = JsonContent.Create(body) };
    private static HttpResponseMessage Problem(HttpStatusCode s, string code, string title) =>
        new(s) { Content = new StringContent(JsonSerializer.Serialize(new { title, code }), System.Text.Encoding.UTF8, "application/problem+json") };

    private async Task<EntryFormViewModel> FormAsync()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 42, "E42", "Inspector", "QualityInspector"));
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/phenomena")
            ? Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen }) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var vm = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await vm.OnNavigatedToAsync(new Dictionary<string, object> { ["shop"] = Shop, ["stage"] = Stage, ["machine"] = Machine });
        vm.SelectedPhenomenon = vm.Phenomena.Single();
        vm.QuantityText = "3"; vm.Remarks = "edge crack";
        _http.Calls.Clear();
        return vm;
    }

    private static Guid PostedSubmissionId(string body) => JsonDocument.Parse(body).RootElement.GetProperty("submissionId").GetGuid();

    [Fact]
    public async Task Double_tap_sends_one_request()
    {
        var vm = await FormAsync();
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        _http.Respond = _ => gate.Task;
        var first = vm.SubmitAsync(); var second = vm.SubmitAsync();
        Assert.False(vm.CanSubmit);
        gate.SetResult(Json(HttpStatusCode.Created, new CreateEntryResult(new EntryDto(1, vm.SubmissionId, 1, "GDC", 1, "GDC_SHOP", "GDC Shop", 5, "M-05", "Press 5", 9, "Cold Shot", 3, 42, "E42", "edge crack", DateTime.UtcNow), false)));
        await Task.WhenAll(first, second);
        Assert.Single(_http.Calls);
        Assert.Equal("confirmation", _nav.Calls.Last().Route);
    }

    [Fact]
    public async Task Lost_response_keeps_the_form_and_the_retry_reuses_the_submission_id()
    {
        var vm = await FormAsync();
        _http.Respond = _ => throw new HttpRequestException("connection reset");
        await vm.SubmitAsync();
        Assert.Contains("may not have been saved", vm.ErrorMessage);
        Assert.Contains("will not create a duplicate", vm.ErrorMessage);
        Assert.Equal(("3", "edge crack", Phen), (vm.QuantityText, vm.Remarks, vm.SelectedPhenomenon));

        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new CreateEntryResult(new EntryDto(1, vm.SubmissionId, 1, "GDC", 1, "GDC_SHOP", "GDC Shop", 5, "M-05", "Press 5", 9, "Cold Shot", 3, 42, "E42", "edge crack", DateTime.UtcNow), true)));
        await vm.SubmitAsync();
        Assert.Equal(2, _http.Calls.Count);
        Assert.Equal(PostedSubmissionId(_http.Calls[0].Body!), PostedSubmissionId(_http.Calls[1].Body!));
        Assert.True((bool)_nav.Calls.Last().Args!["already"]);
    }

    [Fact]
    public async Task Validation_error_is_not_reported_as_an_unknown_outcome()
    {
        var vm = await FormAsync();
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.BadRequest, "quantity_invalid", "Quantity must be a whole number from 1 to 9999."));
        await vm.SubmitAsync();
        Assert.Equal("Quantity must be a whole number from 1 to 9999.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Session_expiry_during_save_keeps_the_entry_and_login_resumes_it_with_the_same_submission_id()
    {
        var vm = await FormAsync();
        var originalId = vm.SubmissionId;
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.Unauthorized, "token_expired", "expired"));
        await vm.SubmitAsync();
        Assert.False(_session.IsAuthenticated);

        // Sign in again (same user).
        _http.Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/login")
            ? Json(HttpStatusCode.OK, new LoginResponse("tok2", DateTime.UtcNow.AddHours(8), 42, "E42", "Inspector", "QualityInspector"))
            : Json(HttpStatusCode.OK, new List<PhenomenonDto> { Phen }));
        var login = new LoginViewModel(_api, _nav, _session, _drafts) { Username = "E42", Password = "secret-password" };
        await login.LoginAsync();
        var (route, args) = _nav.Calls.Last();
        Assert.Equal("entry", route);

        var resumed = new EntryFormViewModel(_api, _nav, _session, _drafts, _dialogs);
        await resumed.OnNavigatedToAsync(args!);
        Assert.Equal((originalId, "3", "edge crack", Phen), (resumed.SubmissionId, resumed.QuantityText, resumed.Remarks, resumed.SelectedPhenomenon));
        Assert.Contains("will not create a duplicate", resumed.ErrorMessage);
    }

    [Fact]
    public async Task A_different_user_signing_in_does_not_get_someone_elses_draft()
    {
        _drafts.Save(new EntryDraft(42, Shop, Stage, Machine, 9, "3", "x", Guid.NewGuid()));
        _http.Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new LoginResponse("t", DateTime.UtcNow.AddHours(8), 77, "E77", "Other", "QualityInspector")));
        await new LoginViewModel(_api, _nav, _session, _drafts) { Username = "E77", Password = "pw-1234567890" }.LoginAsync();
        Assert.Equal("//home", _nav.Calls.Last().Route);
        Assert.Null(_drafts.Take(42));
    }

    [Fact]
    public async Task Inactive_machine_alerts_and_returns_to_the_machine_list()
    {
        var vm = await FormAsync();
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.Conflict, "machine_inactive", "The selected machine is no longer active."));
        await vm.SubmitAsync();
        Assert.Equal("Machine no longer active", Assert.Single(_dialogs.Alerts));
        Assert.Equal("..", _nav.Calls.Last().Route);
    }

    [Fact]
    public async Task Inactive_phenomenon_reloads_the_list_and_clears_the_choice()
    {
        var vm = await FormAsync();
        _http.Respond = r => Task.FromResult(r.Method == HttpMethod.Post
            ? Problem(HttpStatusCode.Conflict, "phenomenon_inactive", "No longer active.")
            : Json(HttpStatusCode.OK, new List<PhenomenonDto>()));   // the reloaded list no longer has it
        await vm.SubmitAsync();
        Assert.Null(vm.SelectedPhenomenon);
        Assert.Empty(vm.Phenomena);
        Assert.Contains(_http.Calls, c => c.Path.EndsWith("/phenomena"));
    }

    [Fact]
    public async Task History_pages_and_discloses_incomplete_results()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 1, "S1", "Sup", "Supervisor"));
        EntryDto E(long id) => new(id, Guid.NewGuid(), 1, "GDC", 1, "GDC_SHOP", "GDC Shop", 5, "M-05", "Press 5", 9, "Cold Shot", 1, 42, "E42", null, DateTime.UtcNow);
        _http.Respond = r => Task.FromResult(r.RequestUri!.Query.Contains("cursor=")
            ? Json(HttpStatusCode.OK, new EntryPage(new[] { E(1) }, null, Array.Empty<string>()))
            : Json(HttpStatusCode.OK, new EntryPage(new[] { E(3), E(2) }, "c1", new[] { "CNC" })));
        var vm = new MyEntriesViewModel(_api, _session);
        await vm.LoadAsync();
        Assert.Equal(2, vm.Entries.Count);
        Assert.True(vm.HasMore);
        Assert.True(vm.IsIncomplete);
        Assert.Contains("CNC", vm.IncompleteNotice);
        await vm.LoadMoreAsync();
        Assert.Equal(new long[] { 3, 2, 1 }, vm.Entries.Select(e => e.Id));
        Assert.False(vm.HasMore);
        Assert.False(vm.IsIncomplete);
        Assert.Contains(_http.Calls, c => c.Path.Contains("/api/rejection-entries/page?limit=50&cursor=c1"));
    }

    [Fact]
    public async Task Admin_edit_sends_the_version_the_screen_was_built_from()
    {
        await _session.StartAsync(new LoginResponse("tok", DateTime.UtcNow.AddHours(8), 1, "A1", "Admin", "Administrator"));
        _http.Respond = _ => Task.FromResult(Problem(HttpStatusCode.Conflict, "concurrent_edit", "This record was changed by someone else after you opened it. Go back, refresh and try again."));
        var r = await _api.AdminMachineUpdateAsync(5, new UpdateMachineRequest("M-05", "Press five", "AAAAAAAAB9E="));
        Assert.Equal("concurrent_edit", r.Error!.Code);
        Assert.Contains("changed by someone else", r.Error.Message);
        Assert.Contains("\"version\":\"AAAAAAAAB9E=\"", _http.Calls.Single().Body);
    }
}
