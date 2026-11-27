// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class PhenomenonEditorViewModel : BaseViewModel
{
	public const int MaxName = 200;

	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private AdminPhenomenonDto? _editing;

	private string _name = "";

	private bool _isActive = true;

	private Option? _shop;

	private Option? _stage;

	private bool _initialising;

	public bool IsEdit => (object)_editing != null;

	public bool IsCreate => (object)_editing == null;

	public ObservableCollection<Option> Shops { get; } = new ObservableCollection<Option>();

	public ObservableCollection<Option> Stages { get; } = new ObservableCollection<Option>();

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			if (Set(ref _name, value, "Name"))
			{
				OnPropertyChanged("NameCounter");
			}
		}
	}

	public string NameCounter => $"{Name.Length}/{200}";

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
				LoadStagesAsync(null);
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
			Set(ref _stage, value, "SelectedStage");
		}
	}

	public string FixedStageText => ((object)_editing == null) ? "" : (_editing.StageName + " · " + string.Join(", ", _editing.ShopCodes));

	public AsyncCommand SaveCommand { get; }

	public AsyncCommand CancelCommand { get; }

	public PhenomenonEditorViewModel(IApiClient api, INavigator nav)
	{
		_api = api;
		_nav = nav;
		SaveCommand = new AsyncCommand(SaveAsync);
		CancelCommand = new AsyncCommand(() => _nav.GoBackAsync());
	}

	public override async Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		_editing = BaseViewModel.Arg<AdminPhenomenonDto>(args, "edit");
		Title = (IsEdit ? "Edit phenomenon" : "Add phenomenon");
		OnPropertyChanged("IsEdit");
		OnPropertyChanged("IsCreate");
		OnPropertyChanged("FixedStageText");
		if ((object)_editing != null)
		{
			Name = _editing.PhenomenonName;
			IsActive = _editing.IsActive;
			return;
		}
		int? preShop = ((args.TryGetValue("shopId", out object s) && s is int si) ? new int?(si) : ((int?)null));
		int? preStage = ((args.TryGetValue("stageId", out object g) && g is int gi) ? new int?(gi) : ((int?)null));
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
				Shops.Clear();
				foreach (ShopDto sh in r.Value)
				{
					Shops.Add(new Option(sh.Id, sh.ShopName));
				}
				SelectedShop = Shops.FirstOrDefault((Option o) => o.Id == preShop);
				_initialising = false;
				if ((object)SelectedShop != null)
				{
					await LoadStagesAsync(preStage);
				}
			}
		});
	}

	private int _stageSeq;

	private async Task LoadStagesAsync(int? preselect)
	{
		// Review fix: two quick shop changes: only the latest shop's stages may fill the list.
		int seq = Interlocked.Increment(ref _stageSeq);
		Stages.Clear();
		SelectedStage = null;
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
		if (num2 == 0)
		{
			return;
		}
		ApiResult<List<StageDto>> r = await _api.GetStagesAsync(sid);
		if (seq != Volatile.Read(ref _stageSeq))
		{
			return;
		}
		if (!r.IsSuccess)
		{
			ErrorMessage = r.Error.Message;
			return;
		}
		foreach (StageDto st in r.Value)
		{
			Stages.Add(new Option(st.Id, st.StageName));
		}
		SelectedStage = Stages.FirstOrDefault((Option o) => o.Id == preselect) ?? ((Stages.Count == 1) ? Stages[0] : null);
	}

	public async Task SaveAsync()
	{
		string name = (Name ?? "").Trim();
		if (name.Length == 0)
		{
			ErrorMessage = "Enter a phenomenon name.";
			return;
		}
		if (name.Length > 200)
		{
			ErrorMessage = $"The name may not exceed {200} characters.";
			return;
		}
		if (IsCreate && (!(SelectedShop?.Id).HasValue || !(SelectedStage?.Id).HasValue))
		{
			ErrorMessage = "Select a shop and an inspection stage.";
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<AdminPhenomenonDto> apiResult = ((!IsEdit) ? (await _api.AdminCreateAsync(new CreatePhenomenonRequest(SelectedShop.Id.Value, SelectedStage.Id.Value, name, IsActive))) : (await _api.AdminUpdateAsync(_editing.Id, new UpdatePhenomenonRequest(name, _editing.Version))));
			ApiResult<AdminPhenomenonDto> r = apiResult;
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
