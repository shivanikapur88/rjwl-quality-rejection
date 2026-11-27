// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class HomeViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private readonly ISession _session;

	public ObservableCollection<ShopDto> Shops { get; } = new ObservableCollection<ShopDto>();

	public string Welcome => ((object)_session.User == null) ? "" : (_session.User.DisplayName + " (" + _session.User.EmployeeCode + ")");

	public string RoleText
	{
		get
		{
			string text = _session.User?.Role;
			if (1 == 0)
			{
			}
			string result = text switch
			{
				"QualityInspector" => "Quality Inspector", 
				"Administrator" => "Administrator", 
				"Supervisor" => "Supervisor", 
				_ => "", 
			};
			if (1 == 0)
			{
			}
			return result;
		}
	}

	public bool CanRecord => _session.IsInspector;

	public bool IsAdmin => _session.IsAdministrator;

	/// <summary>Round 2: supervisors and administrators see the shift summary and decide correction requests.</summary>
	public bool CanReview => _session.IsSupervisor || _session.IsAdministrator;

	public string CorrectionsButtonText => _session.IsInspector ? "My correction requests" : "Correction requests";

	public AsyncCommand ScanCommand { get; private set; } = null!;

	public AsyncCommand SummaryCommand { get; private set; } = null!;

	public AsyncCommand CorrectionsCommand { get; private set; } = null!;

	public string EntriesButtonText => _session.IsInspector ? "My entries" : "View entries";

	public AsyncCommand<ShopDto> SelectShopCommand { get; }

	public AsyncCommand MyEntriesCommand { get; }

	public AsyncCommand AdminCommand { get; }

	public AsyncCommand AdminMachinesCommand { get; }

	public AsyncCommand AdminUsersCommand { get; }

	public AsyncCommand SettingsCommand { get; }

	public AsyncCommand RefreshCommand { get; }

	public HomeViewModel(IApiClient api, INavigator nav, ISession session)
	{
		_api = api;
		_nav = nav;
		_session = session;
		Title = "Home";
		SelectShopCommand = new AsyncCommand<ShopDto>(SelectShopAsync);
		MyEntriesCommand = new AsyncCommand(() => _nav.GoToAsync("myentries"));
		AdminCommand = new AsyncCommand(() => _nav.GoToAsync("admin"));
		AdminMachinesCommand = new AsyncCommand(() => _nav.GoToAsync("adminmachines"));
		AdminUsersCommand = new AsyncCommand(() => _nav.GoToAsync("adminusers"));
		SettingsCommand = new AsyncCommand(() => _nav.GoToAsync("settings"));
		RefreshCommand = new AsyncCommand(LoadAsync);
		ScanCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Scan));
		SummaryCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Summary));
		CorrectionsCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Corrections));
	}

	public override async Task OnAppearingAsync()
	{
		if (_session.User is not null && !_session.IsAuthenticated)
		{
			// Review fix: the token ran out while the app was open (no request has failed yet). Without this, Home shows a
			// name but no role and no buttons; the expiry path returns to sign-in (and keeps nothing of the old session).
			_session.NotifyExpired();
			return;
		}
		OnPropertyChanged("Welcome");
		OnPropertyChanged("RoleText");
		OnPropertyChanged("CanRecord");
		OnPropertyChanged("IsAdmin");
		OnPropertyChanged("EntriesButtonText");
		OnPropertyChanged(nameof(CanReview));
		OnPropertyChanged(nameof(CorrectionsButtonText));
		if (CanRecord)
		{
			await LoadAsync();
		}
	}

	private Task SelectShopAsync(ShopDto shop)
	{
		return RunBusyAsync(async () =>
		{
			ApiResult<List<StageDto>> r = await _api.GetStagesAsync(shop.Id);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				List<StageDto> stages = r.Value;
				if (stages.Count == 0)
				{
					ErrorMessage = "No inspection stage is configured for this shop. Contact your administrator.";
				}
				else if (stages.Count == 1)
				{
					await _nav.GoToAsync("machines", new Dictionary<string, object>
					{
						["shop"] = shop,
						["stage"] = stages[0]
					});
				}
				else
				{
					await _nav.GoToAsync("stages", new Dictionary<string, object> { ["shop"] = shop });
				}
			}
		});
	}

	public Task LoadAsync()
	{
		return RunBusyAsync(async () =>
		{
			ApiResult<List<ShopDto>> r = await _api.GetShopsAsync();
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				Shops.Clear();
				foreach (ShopDto s in r.Value)
				{
					Shops.Add(s);
				}
				if (Shops.Count == 0)
				{
					ErrorMessage = "No shops are assigned to your account. Contact your administrator.";
				}
			}
		});
	}
}
