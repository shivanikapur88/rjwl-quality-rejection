// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class MachineViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private ShopDto? _shop;

	private StageDto? _stage;

	public ObservableCollection<MachineDto> Machines { get; } = new ObservableCollection<MachineDto>();

	public AsyncCommand<MachineDto> SelectMachineCommand { get; }

	public string ContextText => ((object)_shop == null || (object)_stage == null) ? "" : (_shop.ShopName + " › " + _stage.StageName);

	public MachineViewModel(IApiClient api, INavigator nav)
	{
		_api = api;
		_nav = nav;
		SelectMachineCommand = new AsyncCommand<MachineDto>(Select);
	}

	private bool _loaded;

	public override async Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		_shop = BaseViewModel.Arg<ShopDto>(args, "shop");
		_stage = BaseViewModel.Arg<StageDto>(args, "stage");
		Title = "Machine";
		OnPropertyChanged("ContextText");
		if ((object)_shop == null || (object)_stage == null)
		{
			ErrorMessage = "No stage selected.";
			return;
		}
		await LoadAsync();
	}

	/// <summary>Changed: returning to this screen (e.g. after "machine no longer active") reloads the active machines.</summary>
	public override Task OnAppearingAsync() => _loaded && _shop is not null && _stage is not null ? LoadAsync() : Task.CompletedTask;

	public async Task LoadAsync()
	{
		_loaded = true;
		await RunBusyAsync(async () =>
		{
			ApiResult<List<MachineDto>> r = await _api.GetMachinesAsync(_shop.Id, _stage.Id);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				Machines.Clear();
				foreach (MachineDto m in r.Value)
				{
					Machines.Add(m);
				}
				if (Machines.Count == 0)
				{
					ErrorMessage = "No machines are configured for this shop. Contact your administrator.";
				}
			}
		});
	}

	private Task Select(MachineDto m)
	{
		return _nav.GoToAsync("entry", new Dictionary<string, object>
		{
			["shop"] = _shop,
			["stage"] = _stage,
			["machine"] = m
		});
	}
}
