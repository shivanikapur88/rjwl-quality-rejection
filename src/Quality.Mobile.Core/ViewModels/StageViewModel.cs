// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class StageViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private ShopDto? _shop;

	public ObservableCollection<StageDto> Stages { get; } = new ObservableCollection<StageDto>();

	public AsyncCommand<StageDto> SelectStageCommand { get; }

	public string ShopName => _shop?.ShopName ?? "";

	public StageViewModel(IApiClient api, INavigator nav)
	{
		_api = api;
		_nav = nav;
		SelectStageCommand = new AsyncCommand<StageDto>(Select);
	}

	public override async Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		_shop = BaseViewModel.Arg<ShopDto>(args, "shop");
		Title = "Inspection stage";
		OnPropertyChanged("ShopName");
		if ((object)_shop == null)
		{
			ErrorMessage = "No shop selected.";
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<List<StageDto>> r = await _api.GetStagesAsync(_shop.Id);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				Stages.Clear();
				foreach (StageDto s in r.Value)
				{
					Stages.Add(s);
				}
			}
		});
	}

	private Task Select(StageDto stage)
	{
		return _nav.GoToAsync("machines", new Dictionary<string, object>
		{
			["shop"] = _shop,
			["stage"] = stage
		});
	}
}
