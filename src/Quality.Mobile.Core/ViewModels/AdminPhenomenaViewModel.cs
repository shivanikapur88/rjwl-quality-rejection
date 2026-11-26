// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class AdminPhenomenaViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private readonly IDialogs _dialogs;

	private Option? _shop;

	private Option? _stage;

	private Option? _status;

	private string _search = "";

	private bool _initialising;

	private int _version;

	public ObservableCollection<Option> Shops { get; } = new ObservableCollection<Option>();

	public ObservableCollection<Option> Stages { get; } = new ObservableCollection<Option>();

	public List<Option> StatusOptions { get; }

	public ObservableCollection<AdminItem> Items { get; } = new ObservableCollection<AdminItem>();

	public string CountText => $"{Items.Count} shown";

	public Option? SelectedShop
	{
		get
		{
			return _shop;
		}
		set
		{
			if (Set(ref _shop, value, "SelectedShop") && !_initialising)
			{
				OnShopChangedAsync();
			}
		}
	}

	public Option? SelectedStage
	{
		get
		{
			return _stage;
		}
		set
		{
			if (Set(ref _stage, value, "SelectedStage") && !_initialising)
			{
				LoadAsync();
			}
		}
	}

	public Option? SelectedStatus
	{
		get
		{
			return _status;
		}
		set
		{
			if (Set(ref _status, value, "SelectedStatus") && !_initialising)
			{
				LoadAsync();
			}
		}
	}

	public string SearchText
	{
		get
		{
			return _search;
		}
		set
		{
			Set(ref _search, value, "SearchText");
		}
	}

	public AsyncCommand SearchCommand { get; }

	public AsyncCommand AddCommand { get; }

	public AsyncCommand<AdminItem> EditCommand { get; }

	public AsyncCommand<AdminItem> ToggleCommand { get; }

	public AsyncCommand<AdminItem> DeleteCommand { get; }

	public AdminPhenomenaViewModel(IApiClient api, INavigator nav, IDialogs dialogs)
	{
		_api = api;
		_nav = nav;
		_dialogs = dialogs;
		Title = "Rejection phenomena";
		StatusOptions = new List<Option>
		{
			new Option(null, "All"),
			new Option(1, "Active"),
			new Option(0, "Inactive")
		};
		_status = StatusOptions[0];
		SearchCommand = new AsyncCommand(LoadAsync);
		AddCommand = new AsyncCommand(AddAsync);
		EditCommand = new AsyncCommand<AdminItem>((AdminItem i) => _nav.GoToAsync("editor", new Dictionary<string, object> { ["edit"] = i.Dto }));
		ToggleCommand = new AsyncCommand<AdminItem>(ToggleAsync);
		DeleteCommand = new AsyncCommand<AdminItem>(DeleteAsync);
	}

	public override async Task OnAppearingAsync()
	{
		if (Shops.Count == 0)
		{
			await RunBusyAsync(async () =>
			{
				ApiResult<List<ShopDto>> r = await _api.GetShopsAsync();
				if (!r.IsSuccess)
				{
					ErrorMessage = r.Error.Message;
				}
				else
				{
					_initialising = true;
					Shops.Add(new Option(null, "All shops"));
					foreach (ShopDto s in r.Value)
					{
						Shops.Add(new Option(s.Id, s.ShopName));
					}
					Stages.Add(new Option(null, "All stages"));
					SelectedShop = Shops[0];
					SelectedStage = Stages[0];
					_initialising = false;
				}
			});
		}
		await LoadAsync();
	}

	private async Task OnShopChangedAsync()
	{
		_initialising = true;
		Stages.Clear();
		Stages.Add(new Option(null, "All stages"));
		SelectedStage = Stages[0];
		int? num = _shop?.Id;
		int sid = default;
		int num2;
		if (num.HasValue)
		{
			sid = num.GetValueOrDefault();
			num2 = 1;
		}
		else
		{
			num2 = 0;
		}
		if (num2 != 0)
		{
			ApiResult<List<StageDto>> r = await _api.GetStagesAsync(sid);
			if (r.IsSuccess)
			{
				foreach (StageDto s in r.Value)
				{
					Stages.Add(new Option(s.Id, s.StageName));
				}
			}
			else
			{
				ErrorMessage = r.Error.Message;
			}
		}
		_initialising = false;
		await LoadAsync();
	}

	public async Task LoadAsync()
	{
		int my = ++_version;
		IsBusy = true;
		ErrorMessage = null;
		try
		{
			int? num = _status?.Id;
			if (1 == 0)
			{
			}
			if (!num.HasValue)
			{
				goto IL_00c1;
			}
			switch (num.GetValueOrDefault())
			{
			case 1:
				break;
			case 0:
				goto IL_00b8;
			default:
				goto IL_00c1;
			}
			bool? flag = true;
			goto IL_00ce;
			IL_00b8:
			flag = false;
			goto IL_00ce;
			IL_00c1:
			flag = null;
			goto IL_00ce;
			IL_00ce:
			if (1 == 0)
			{
			}
			bool? active = flag;
			ApiResult<List<AdminPhenomenonDto>> r = await _api.AdminListAsync(_shop?.Id, _stage?.Id, active, _search);
			if (my != _version)
			{
				return;
			}
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
				return;
			}
			Items.Clear();
			foreach (AdminPhenomenonDto d in r.Value)
			{
				Items.Add(new AdminItem(d));
			}
			OnPropertyChanged("CountText");
		}
		catch (Exception)
		{
			ErrorMessage = "Something went wrong. Please try again.";
		}
		finally
		{
			if (my == _version)
			{
				IsBusy = false;
			}
		}
	}

	private Task AddAsync()
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		int? num = _shop?.Id;
		if (num.HasValue)
		{
			int valueOrDefault = num.GetValueOrDefault();
			if (true)
			{
				dictionary["shopId"] = valueOrDefault;
			}
		}
		num = _stage?.Id;
		if (num.HasValue)
		{
			int valueOrDefault2 = num.GetValueOrDefault();
			if (true)
			{
				dictionary["stageId"] = valueOrDefault2;
			}
		}
		return _nav.GoToAsync("editor", dictionary);
	}

	public async Task ToggleAsync(AdminItem item)
	{
		bool isActive = item.IsActive;
		bool flag = isActive;
		if (flag)
		{
			flag = !(await _dialogs.ConfirmAsync("Deactivate phenomenon", "Deactivate '" + item.Name + "'?\nInspectors will no longer be able to select it. Existing entries are not changed.", "Deactivate", "Cancel"));
		}
		if (flag)
		{
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<AdminPhenomenonDto> r = await _api.AdminSetStatusAsync(item.Dto.Id, !item.IsActive);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				int idx = Items.IndexOf(item);
				if (idx >= 0)
				{
					Items[idx] = new AdminItem(r.Value);
				}
			}
		});
	}

	public async Task DeleteAsync(AdminItem item)
	{
		if (!(await _dialogs.ConfirmAsync("Delete permanently", "Permanently delete '" + item.Name + "'?\nThis only works if it has never been used in a rejection entry. Otherwise deactivate it instead.", "Delete", "Cancel")))
		{
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<Unit> r = await _api.AdminDeleteAsync(item.Dto.Id);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				Items.Remove(item);
				OnPropertyChanged("CountText");
			}
		});
	}
}
