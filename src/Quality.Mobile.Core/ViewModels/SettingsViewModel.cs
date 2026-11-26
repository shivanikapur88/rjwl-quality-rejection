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

	public SettingsViewModel(IApiClient api, IAppSettings settings, ISession session, INavigator nav, IDialogs dialogs)
	{
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

	private bool TrySave()
	{
		ErrorMessage = null;
		InfoMessage = null;
		if (!BaseUrlValidator.TryNormalize(ServerUrl, out string normalized, out string error))
		{
			ErrorMessage = error;
			return false;
		}
		_settings.BaseUrl = normalized;
		ServerUrl = normalized;
		return true;
	}

	public Task SaveAsync()
	{
		if (TrySave())
		{
			InfoMessage = "Server address saved.";
		}
		return Task.CompletedTask;
	}

	public async Task TestAsync()
	{
		if (!TrySave())
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
	}

	public async Task LogoutAsync()
	{
		if (await _dialogs.ConfirmAsync("Sign out", "Sign out of this device?", "Sign out", "Cancel"))
		{
			await _session.EndAsync();
			await _nav.GoToAsync("//login");
		}
	}
}
