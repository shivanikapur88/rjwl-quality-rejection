// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class LoginViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private readonly ISession _session;

	private readonly EntryDraftStore _drafts;

	private string _username = "";

	private string _password = "";

	private bool _restoreTried;

	public string Username
	{
		get
		{
			return _username;
		}
		set
		{
			Set(ref _username, value, "Username");
		}
	}

	public string Password
	{
		get
		{
			return _password;
		}
		set
		{
			Set(ref _password, value, "Password");
		}
	}

	public AsyncCommand LoginCommand { get; }

	public AsyncCommand SettingsCommand { get; }

	public LoginViewModel(IApiClient api, INavigator nav, ISession session, EntryDraftStore drafts)
	{
		_api = api;
		_nav = nav;
		_session = session;
		_drafts = drafts;
		Title = "Sign in";
		LoginCommand = new AsyncCommand(LoginAsync);
		SettingsCommand = new AsyncCommand(() => _nav.GoToAsync("settings"));
	}

	public override async Task OnAppearingAsync()
	{
		if (!_restoreTried)
		{
			_restoreTried = true;
			if (await _session.RestoreAsync())
			{
				await _nav.GoToAsync("//home");
			}
		}
	}

	public async Task LoginAsync()
	{
		if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
		{
			ErrorMessage = "Enter your username or employee ID and password.";
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<LoginResponse> r = await _api.LoginAsync(new LoginRequest(Username.Trim(), Password));
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				await _session.StartAsync(r.Value);
				Password = "";
				await _nav.GoToAsync("//home");
				// Changed: resume an entry that was interrupted by session expiry (same user only).
				if (_drafts.Take(r.Value.UserId) is { } d)
					await _nav.GoToAsync("entry", new Dictionary<string, object> { ["shop"] = d.Shop, ["stage"] = d.Stage, ["machine"] = d.Machine, ["draft"] = d });
			}
		});
	}
}
