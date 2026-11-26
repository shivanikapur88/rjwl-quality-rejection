// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class AdminMachinesViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private readonly IDialogs _dialogs;

	private Option? _shop;

	private Option? _status;

	private string _search = "";

	private bool _initialising;

	private int _version;

	public ObservableCollection<Option> Shops { get; } = new ObservableCollection<Option>();

	public List<Option> StatusOptions { get; }

	public ObservableCollection<MachineItem> Items { get; } = new ObservableCollection<MachineItem>();

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

	public AsyncCommand<MachineItem> EditCommand { get; }

	public AsyncCommand<MachineItem> ToggleCommand { get; }

	public AsyncCommand<MachineItem> DeleteCommand { get; }

	public AdminMachinesViewModel(IApiClient api, INavigator nav, IDialogs dialogs)
	{
		_api = api;
		_nav = nav;
		_dialogs = dialogs;
		Title = "Machines";
		StatusOptions = new List<Option>
		{
			new Option(null, "All"),
			new Option(1, "Active"),
			new Option(0, "Inactive")
		};
		_status = StatusOptions[0];
		SearchCommand = new AsyncCommand(LoadAsync);
		AddCommand = new AsyncCommand(() =>
		{
			INavigator nav2 = _nav;
			int? num = _shop?.Id;
			Dictionary<string, object> args;
			if (num.HasValue)
			{
				int valueOrDefault = num.GetValueOrDefault();
				args = new Dictionary<string, object> { ["shopId"] = valueOrDefault };
			}
			else
			{
				args = new Dictionary<string, object>();
			}
			return nav2.GoToAsync("machineeditor", args);
		});
		EditCommand = new AsyncCommand<MachineItem>((MachineItem i) => _nav.GoToAsync("machineeditor", new Dictionary<string, object> { ["editMachine"] = i.Dto }));
		ToggleCommand = new AsyncCommand<MachineItem>(ToggleAsync);
		DeleteCommand = new AsyncCommand<MachineItem>(DeleteAsync);
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
					SelectedShop = Shops[0];
					_initialising = false;
				}
			});
		}
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
			ApiResult<List<AdminMachineDto>> r = await _api.AdminMachinesListAsync(_shop?.Id, active, _search);
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
			foreach (AdminMachineDto d in r.Value)
			{
				Items.Add(new MachineItem(d));
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

	public async Task ToggleAsync(MachineItem item)
	{
		bool isActive = item.IsActive;
		bool flag = isActive;
		if (flag)
		{
			flag = !(await _dialogs.ConfirmAsync("Deactivate machine", $"Deactivate '{item.Code}' ({item.ShopName})?\nInspectors will no longer be able to select it. Existing entries are not changed.", "Deactivate", "Cancel"));
		}
		if (flag)
		{
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<AdminMachineDto> r = await _api.AdminMachineSetStatusAsync(item.Dto.Id, !item.IsActive, item.Dto.Version);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				int idx = Items.IndexOf(item);
				if (idx >= 0)
				{
					Items[idx] = new MachineItem(r.Value);
				}
			}
		});
	}

	public async Task DeleteAsync(MachineItem item)
	{
		if (!(await _dialogs.ConfirmAsync("Delete permanently", $"Permanently delete machine '{item.Code}' ({item.ShopName})?\nThis only works if it has never been used in a rejection entry. Otherwise deactivate it instead.", "Delete", "Cancel")))
		{
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<Unit> r = await _api.AdminMachineDeleteAsync(item.Dto.Id);
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
