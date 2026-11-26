// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class EntryFormViewModel : BaseViewModel
{
	public const int MaxRemarks = 500;

	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private ShopDto? _shop;

	private StageDto? _stage;

	private MachineDto? _machine;

	private List<PhenomenonDto> _all = new List<PhenomenonDto>();

	private PhenomenonDto? _selected;

	private string _remarks = "";

	private string _search = "";

	private string _qty = "1";

	private bool _submitting;

	private bool _submittedOk;

	public static int MaxQuantity => 9999;

	public Guid SubmissionId { get; private set; } = Guid.NewGuid();

	public ObservableCollection<PhenomenonDto> Phenomena { get; } = new ObservableCollection<PhenomenonDto>();

	public AsyncCommand SubmitCommand { get; }

	public AsyncCommand ResetCommand { get; }

	public AsyncCommand IncreaseCommand { get; }

	public AsyncCommand DecreaseCommand { get; }

	public string QuantityText
	{
		get
		{
			return _qty;
		}
		set
		{
			if (Set(ref _qty, value ?? "", "QuantityText"))
			{
				OnPropertyChanged("HasQuantityError");
				OnPropertyChanged("SummaryText");
				OnPropertyChanged("CanSubmit");
				SubmitCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public int? ParsedQuantity
	{
		get
		{
			int result;
			return (int.TryParse(_qty.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 1 && result <= MaxQuantity) ? new int?(result) : ((int?)null);
		}
	}

	public bool HasQuantityError => !ParsedQuantity.HasValue;

	public string QuantityHint => $"Enter a whole number from 1 to {MaxQuantity}";

	public string ShopText => _shop?.ShopName ?? "";

	public string StageText => _stage?.StageName ?? "";

	public string MachineText => ((object)_machine == null) ? "" : (_machine.MachineCode + " – " + _machine.MachineName);

	public string SearchText
	{
		get
		{
			return _search;
		}
		set
		{
			if (Set(ref _search, value, "SearchText"))
			{
				ApplyFilter();
			}
		}
	}

	public string Remarks
	{
		get
		{
			return _remarks;
		}
		set
		{
			if (Set(ref _remarks, value ?? "", "Remarks"))
			{
				OnPropertyChanged("RemarksCounter");
				OnPropertyChanged("CanSubmit");
				SubmitCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public string RemarksCounter => $"{Remarks.Length}/{500}";

	public PhenomenonDto? SelectedPhenomenon
	{
		get
		{
			return _selected;
		}
		set
		{
			if (Set(ref _selected, value, "SelectedPhenomenon"))
			{
				OnPropertyChanged("SummaryText");
				OnPropertyChanged("HasSelection");
				OnPropertyChanged("CanSubmit");
				SubmitCommand.RaiseCanExecuteChanged();
			}
		}
	}

	public bool HasSelection => (object)_selected != null;

	public string SummaryText => ((object)_selected == null) ? "Select a rejection phenomenon above" : (_selected.PhenomenonName + "  ×  " + (ParsedQuantity?.ToString() ?? "?"));

	public bool CanSubmit => !_submitting && !IsBusy && (object)_selected != null && ParsedQuantity.HasValue && Remarks.Length <= 500;

	public EntryFormViewModel(IApiClient api, INavigator nav)
	{
		_api = api;
		_nav = nav;
		Title = "Rejection entry";
		SubmitCommand = new AsyncCommand(SubmitAsync, () => CanSubmit);
		ResetCommand = new AsyncCommand(() =>
		{
			Reset();
			return Task.CompletedTask;
		});
		IncreaseCommand = new AsyncCommand(() =>
		{
			Adjust(1);
			return Task.CompletedTask;
		});
		DecreaseCommand = new AsyncCommand(() =>
		{
			Adjust(-1);
			return Task.CompletedTask;
		});
	}

	private void Adjust(int delta)
	{
		QuantityText = Math.Clamp((ParsedQuantity ?? 1) + delta, 1, MaxQuantity).ToString(CultureInfo.InvariantCulture);
	}

	public override async Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		_shop = BaseViewModel.Arg<ShopDto>(args, "shop");
		_stage = BaseViewModel.Arg<StageDto>(args, "stage");
		_machine = BaseViewModel.Arg<MachineDto>(args, "machine");
		OnPropertyChanged("ShopText");
		OnPropertyChanged("StageText");
		OnPropertyChanged("MachineText");
		if ((object)_shop == null || (object)_stage == null || (object)_machine == null)
		{
			ErrorMessage = "Selection incomplete. Go back and choose shop, stage and machine.";
		}
		else
		{
			await LoadPhenomenaAsync();
		}
	}

	public override Task OnAppearingAsync()
	{
		if (_submittedOk)
		{
			_submittedOk = false;
			Reset();
		}
		return Task.CompletedTask;
	}

	public Task LoadPhenomenaAsync()
	{
		return RunBusyAsync(async () =>
		{
			ApiResult<List<PhenomenonDto>> r = await _api.GetPhenomenaAsync(_shop.Id, _stage.Id);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				_all = r.Value;
				ApplyFilter();
				if (_all.Count == 0)
				{
					ErrorMessage = "No active rejection phenomena are configured for this stage. Contact your administrator.";
				}
			}
		});
	}

	private void ApplyFilter()
	{
		string q = (_search ?? "").Trim();
		Phenomena.Clear();
		foreach (PhenomenonDto item in _all.Where((PhenomenonDto p) => q.Length == 0 || p.PhenomenonName.Contains(q, StringComparison.OrdinalIgnoreCase)))
		{
			Phenomena.Add(item);
		}
	}

	public void Reset()
	{
		SelectedPhenomenon = null;
		Remarks = "";
		SearchText = "";
		QuantityText = "1";
		ErrorMessage = null;
		SubmissionId = Guid.NewGuid();
		OnPropertyChanged("SubmissionId");
	}

	public async Task SubmitAsync()
	{
		if (_submitting)
		{
			return;
		}
		if ((object)_selected == null)
		{
			ErrorMessage = "Select a rejection phenomenon.";
			return;
		}
		int? parsedQuantity = ParsedQuantity;
		int quantity = default;
		int num;
		if (parsedQuantity.HasValue)
		{
			quantity = parsedQuantity.GetValueOrDefault();
			num = 1;
		}
		else
		{
			num = 0;
		}
		if (num == 0)
		{
			ErrorMessage = QuantityHint + ".";
			return;
		}
		if (Remarks.Length > 500)
		{
			ErrorMessage = $"Remarks may not exceed {500} characters.";
			return;
		}
		_submitting = true;
		SubmitCommand.RaiseCanExecuteChanged();
		try
		{
			IsBusy = true;
			ErrorMessage = null;
			CreateEntryRequest req = new CreateEntryRequest(SubmissionId, _shop.Id, _stage.Id, _machine.Id, _selected.Id, quantity, string.IsNullOrWhiteSpace(Remarks) ? null : Remarks.Trim());
			ApiResult<CreateEntryResult> r = await _api.SubmitEntryAsync(req);
			if (r.IsSuccess)
			{
				_submittedOk = true;
				await _nav.GoToAsync("confirmation", new Dictionary<string, object>
				{
					["entry"] = r.Value.Entry,
					["already"] = r.Value.AlreadySubmitted
				});
				return;
			}
			ApiError e = r.Error;
			switch (e.Code)
			{
			case "phenomenon_inactive":
			case "phenomenon_not_found":
			case "phenomenon_not_in_stage":
				SelectedPhenomenon = null;
				ErrorMessage = e.Message;
				IsBusy = false;
				await LoadPhenomenaAsync();
				ErrorMessage = e.Message;
				break;
			case "submission_payload_mismatch":
				SubmissionId = Guid.NewGuid();
				OnPropertyChanged("SubmissionId");
				ErrorMessage = "An earlier attempt of this entry may already be saved with different details. Check My entries before saving again.";
				break;
			default:
				ErrorMessage = (e.OutcomeUnknown ? (e.Message + " Your entry may not have been saved. Tap Save again – it will not create a duplicate.") : e.Message);
				break;
			}
		}
		catch (Exception)
		{
			ErrorMessage = "Something went wrong. Please try again. Your entry may not have been saved. Tap Save again – it will not create a duplicate.";
		}
		finally
		{
			IsBusy = false;
			_submitting = false;
			OnPropertyChanged("CanSubmit");
			SubmitCommand.RaiseCanExecuteChanged();
		}
	}
}
