// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Threading.Tasks;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class SettingsViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly IAppSettings _settings;

	private readonly ISession _session;

	private readonly INavigator _nav;

	private readonly IDialogs _dialogs;

	private readonly EntryDraftStore? _drafts;

	private string _url;

	private string? _info;

	public string ServerUrl
	{
		get
		{
			return _url;
		}
		set
		{
			Set(ref _url, value, "ServerUrl");
		}
	}

	public string? InfoMessage
	{
		get
		{
			return _info;
		}
		private set
		{
			if (Set(ref _info, value, "InfoMessage"))
			{
				OnPropertyChanged("HasInfo");
			}
		}
	}

	public bool HasInfo => !string.IsNullOrEmpty(_info);

	public bool IsSignedIn => _session.IsAuthenticated;

	public string UserText => ((object)_session.User == null) ? "Not signed in" : $"{_session.User.DisplayName} ({_session.User.EmployeeCode}) – {_session.User.Role}";

	public string VersionText { get; set; } = "Version 1.0";

	public AsyncCommand SaveCommand { get; }

	public AsyncCommand TestCommand { get; }

	public AsyncCommand LogoutCommand { get; }

	public SettingsViewModel(IApiClient api, IAppSettings settings, ISession session, INavigator nav, IDialogs dialogs, EntryDraftStore? drafts = null)
	{
		_drafts = drafts;
		_api = api;
		_settings = settings;
		_session = session;
		_nav = nav;
		_dialogs = dialogs;
		Title = "Settings";
		_url = settings.BaseUrl ?? "";
		SaveCommand = new AsyncCommand(SaveAsync);
		TestCommand = new AsyncCommand(TestAsync);
		LogoutCommand = new AsyncCommand(LogoutAsync);
	}

	public override Task OnAppearingAsync()
	{
		ServerUrl = _settings.BaseUrl ?? "";
		OnPropertyChanged("IsSignedIn");
		OnPropertyChanged("UserText");
		return Task.CompletedTask;
	}

	/// <summary>Saves the address. Returns null when it is not valid; otherwise whether the session was ended because the server changed.</summary>
	private async Task<bool?> TrySaveAsync()
	{
		ErrorMessage = null;
		InfoMessage = null;
		if (!BaseUrlValidator.TryNormalize(ServerUrl, out string normalized, out string error))
		{
			ErrorMessage = error;
			return null;
		}
		var previous = _settings.BaseUrl;
		_settings.BaseUrl = normalized;
		ServerUrl = normalized;
		// Full review #10: a token is only for the server that issued it. A different server ends the session,
		// so the token is never sent to the new address.
		if (_session.IsAuthenticated && !SameServer(previous, normalized))
		{
			_drafts?.Clear();   // the draft's shop, stage and machine belong to the old server
			await _session.EndAsync();
			OnPropertyChanged("IsSignedIn");
			OnPropertyChanged("UserText");
			return true;
		}
		return false;
	}

	/// <summary>The session was ended because the server changed: go to sign-in (otherwise every call fails as "session expired").</summary>
	private async Task SignedOutAsync(string outcome)
	{
		await _dialogs.AlertAsync("Signed out", (outcome + " You were signed out because the server address changed. Sign in to the new server.").TrimStart());
		await _nav.GoToAsync(Routes.Login);
	}

	/// <summary>Review fix: the same deployment means the same scheme, host (any case), port AND path
	/// (https://h/prod and https://h/test are different servers); only a trailing slash is ignored.</summary>
	private static bool SameServer(string? a, string? b) =>
		Uri.TryCreate(a?.Trim(), UriKind.Absolute, out var x) && Uri.TryCreate(b?.Trim(), UriKind.Absolute, out var y)
		&& string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port && x.Scheme == y.Scheme
		&& string.Equals(x.AbsolutePath.TrimEnd('/'), y.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

	public async Task SaveAsync()
	{
		var saved = await TrySaveAsync();
		if (saved is null)
		{
			return;
		}
		InfoMessage = "Server address saved.";
		if (saved == true)
		{
			await SignedOutAsync("Server address saved.");
		}
	}

	public async Task TestAsync()
	{
		var saved = await TrySaveAsync();
		if (saved is null)
		{
			return;
		}
		IsBusy = true;
		try
		{
			ApiResult<Unit> r = await _api.HealthAsync();
			if (r.IsSuccess)
			{
				InfoMessage = "Connected to the server.";
			}
			else
			{
				ErrorMessage = r.Error.Message;
			}
		}
		catch (Exception)
		{
			ErrorMessage = "Something went wrong. Please try again.";
		}
		finally
		{
			IsBusy = false;
		}
		if (saved == true)
		{
			await SignedOutAsync(InfoMessage ?? ErrorMessage ?? "");
		}
	}

	public async Task LogoutAsync()
	{
		if (await _dialogs.ConfirmAsync("Sign out", "Sign out of this device?", "Sign out", "Cancel"))
		{
			_drafts?.Clear();   // review fix: an explicit sign-out discards the entry kept for an expired session
			await _session.EndAsync();
			await _nav.GoToAsync("//login");
		}
	}
}
