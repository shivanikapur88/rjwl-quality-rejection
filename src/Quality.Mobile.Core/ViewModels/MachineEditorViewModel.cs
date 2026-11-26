// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class MachineEditorViewModel : BaseViewModel
{
	public const int MaxCode = 30;

	public const int MaxName = 100;

	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private AdminMachineDto? _editing;

	private string _code = "";

	private string _name = "";

	private bool _isActive = true;

	private Option? _shop;

	public bool IsEdit => (object)_editing != null;

	public bool IsCreate => (object)_editing == null;

	public ObservableCollection<Option> Shops { get; } = new ObservableCollection<Option>();

	public Option? SelectedShop
	{
		get
		{
			return _shop;
		}
		set
		{
			Set(ref _shop, value, "SelectedShop");
		}
	}

	public string FixedShopText => _editing?.ShopName ?? "";

	public string Code
	{
		get
		{
			return _code;
		}
		set
		{
			Set(ref _code, value, "Code");
		}
	}

	public string MachineName
	{
		get
		{
			return _name;
		}
		set
		{
			Set(ref _name, value, "MachineName");
		}
	}

	public bool IsActive
	{
		get
		{
			return _isActive;
		}
		set
		{
			Set(ref _isActive, value, "IsActive");
		}
	}

	public AsyncCommand SaveCommand { get; }

	public AsyncCommand CancelCommand { get; }

	public MachineEditorViewModel(IApiClient api, INavigator nav)
	{
		_api = api;
		_nav = nav;
		SaveCommand = new AsyncCommand(SaveAsync);
		CancelCommand = new AsyncCommand(() => _nav.GoBackAsync());
	}

	public override async Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		_editing = BaseViewModel.Arg<AdminMachineDto>(args, "editMachine");
		Title = (IsEdit ? "Edit machine" : "Add machine");
		OnPropertyChanged("IsEdit");
		OnPropertyChanged("IsCreate");
		OnPropertyChanged("FixedShopText");
		if ((object)_editing != null)
		{
			Code = _editing.MachineCode;
			MachineName = _editing.MachineName;
			IsActive = _editing.IsActive;
			return;
		}
		int? pre = ((args.TryGetValue("shopId", out object s) && s is int si) ? new int?(si) : ((int?)null));
		await RunBusyAsync(async () =>
		{
			ApiResult<List<ShopDto>> r = await _api.GetShopsAsync();
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				Shops.Clear();
				foreach (ShopDto sh in r.Value)
				{
					Shops.Add(new Option(sh.Id, sh.ShopName));
				}
				SelectedShop = Shops.FirstOrDefault((Option o) => o.Id == pre) ?? ((Shops.Count == 1) ? Shops[0] : null);
			}
		});
	}

	public async Task SaveAsync()
	{
		string code = (Code ?? "").Trim();
		string name = (MachineName ?? "").Trim();
		if (code.Length == 0)
		{
			ErrorMessage = "Enter a machine code.";
			return;
		}
		if (code.Length > 30)
		{
			ErrorMessage = $"The machine code may not exceed {30} characters.";
			return;
		}
		if (name.Length == 0)
		{
			ErrorMessage = "Enter a machine name.";
			return;
		}
		if (name.Length > 100)
		{
			ErrorMessage = $"The machine name may not exceed {100} characters.";
			return;
		}
		if (IsCreate && !(SelectedShop?.Id).HasValue)
		{
			ErrorMessage = "Select a shop.";
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<AdminMachineDto> apiResult = ((!IsEdit) ? (await _api.AdminMachineCreateAsync(new CreateMachineRequest(SelectedShop.Id.Value, code, name, IsActive))) : (await _api.AdminMachineUpdateAsync(_editing.Id, new UpdateMachineRequest(code, name, _editing.Version))));
			ApiResult<AdminMachineDto> r = apiResult;
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				await _nav.GoBackAsync();
			}
		});
	}
}
