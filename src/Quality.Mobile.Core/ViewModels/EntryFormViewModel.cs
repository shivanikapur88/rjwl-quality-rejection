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

	private readonly ISession _session;

	private readonly EntryDraftStore _drafts;

	private readonly IDialogs _dialogs;

	private EntryDraft? _resume;

	/// <summary>The draft already put back: Shell can apply the same query again (e.g. coming back to this page), and it must not be restored twice.</summary>
	private EntryDraft? _restored;

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

	/// <summary>Review fix: the last save may have reached the server (timeout, lost response). Until an outcome is
	/// confirmed, Clear keeps the submission id, so re-entering and saving cannot create a duplicate.</summary>
	private bool _outcomeUnknown;

	private bool _loadFailed;

	public static int MaxQuantity => 9999;

	public Guid SubmissionId { get; private set; } = Guid.NewGuid();

	public ObservableCollection<PhenomenonDto> Phenomena { get; } = new ObservableCollection<PhenomenonDto>();

	public AsyncCommand SubmitCommand { get; }

	public AsyncCommand ResetCommand { get; }

	public AsyncCommand IncreaseCommand { get; }

	public AsyncCommand DecreaseCommand { get; }

	/// <summary>Review fix: loads the phenomena list again after it failed to load (a kept draft is restored then).</summary>
	public AsyncCommand RetryCommand { get; }

	/// <summary>The phenomena list failed to load: the "Try again" button is shown.</summary>
	public bool CanRetryLoad
	{
		get
		{
			return _loadFailed;
		}
		private set
		{
			Set(ref _loadFailed, value, "CanRetryLoad");
		}
	}

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

	public EntryFormViewModel(IApiClient api, INavigator nav, ISession session, EntryDraftStore drafts, IDialogs dialogs)
	{
		_dialogs = dialogs;
		_api = api;
		_nav = nav;
		_session = session;
		_drafts = drafts;
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
		RetryCommand = new AsyncCommand(LoadPhenomenaAsync);
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
		EntryDraft? draft = BaseViewModel.Arg<EntryDraft>(args, "draft");
		int? userId = _session.User?.UserId;
		if (draft is null && userId is int du && _shop is not null && _stage is not null && _machine is not null
			&& _drafts.Peek(du) is { } kept && kept.Shop.Id == _shop.Id && kept.Stage.Id == _stage.Id && kept.Machine.Id == _machine.Id)
		{
			draft = kept;   // review fix: a draft not yet restored (e.g. its first phenomena load failed and the form was left) is still resumed
		}
		_resume = (draft is not null && !Equals(draft, _restored)) ? draft : null;
		if (_resume is null && userId is int pu && _shop is not null && _stage is not null && _machine is not null
			&& _drafts.PendingFor(pu, _shop.Id, _stage.Id, _machine.Id) is Guid pending && pending != SubmissionId)
		{
			// Review fix: an earlier save of this form (this user, shop, stage and machine) may have reached the server.
			// The same id is sent again: the server replays it if the entry matches, or answers payload mismatch (new id).
			SubmissionId = pending;
			_outcomeUnknown = true;
			OnPropertyChanged("SubmissionId");
		}
		OnPropertyChanged("ShopText");
		OnPropertyChanged("StageText");
		OnPropertyChanged("MachineText");
		if ((object)_shop == null || (object)_stage == null || (object)_machine == null)
		{
			ErrorMessage = "Selection incomplete. Go back and choose shop, stage and machine.";
		}
		else
		{
			await LoadPhenomenaAsync();   // restores the draft once the phenomena are loaded
		}
	}

	/// <summary>Puts back an entry that was being saved when the session expired, keeping its submission id.</summary>
	private void Restore(EntryDraft d)
	{
		_resume = null;
		_restored = d;
		_drafts.Remove(d);   // on screen now: no longer needed in the store
		SelectedPhenomenon = d.PhenomenonId is int pid ? _all.FirstOrDefault(p => p.Id == pid) : null;
		QuantityText = d.QuantityText;
		Remarks = d.Remarks;
		SubmissionId = d.SubmissionId;
		_outcomeUnknown = _drafts.PendingFor(d.UserId, d.Shop.Id, d.Stage.Id, d.Machine.Id) == d.SubmissionId;   // Clear keeps a pending id
		OnPropertyChanged("SubmissionId");
		ErrorMessage = "Your session expired while this entry was being saved. Check the details and tap Save again - it will not create a duplicate.";
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
		if (_shop is null || _stage is null)
		{
			return Task.CompletedTask;
		}
		return RunBusyAsync(async () =>
		{
			CanRetryLoad = true;   // stays set if the call fails or throws
			ApiResult<List<PhenomenonDto>> r = await _api.GetPhenomenaAsync(_shop.Id, _stage.Id);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				CanRetryLoad = false;
				_all = r.Value;
				ApplyFilter();
				if (_all.Count == 0)
				{
					ErrorMessage = "No active rejection phenomena are configured for this stage. Contact your administrator.";
				}
				else if ((object)_resume != null)
				{
					// Only now: if loading failed, the error stays on screen and the draft is kept for the next attempt.
					Restore(_resume);
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
		if (_outcomeUnknown)
		{
			return;   // same id: if the earlier attempt was saved, the server answers "already saved" or "payload mismatch"
		}
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
		var userId = _session.User?.UserId;   // captured now: an expired session clears it
		var sentId = SubmissionId;
		var answered = false;
		try
		{
			IsBusy = true;
			ErrorMessage = null;
			CreateEntryRequest req = new CreateEntryRequest(SubmissionId, _shop.Id, _stage.Id, _machine.Id, _selected.Id, quantity, string.IsNullOrWhiteSpace(Remarks) ? null : Remarks.Trim());
			ApiResult<CreateEntryResult> r = await _api.SubmitEntryAsync(req);
			answered = true;
			// Review fix: only the entry endpoint's own answer settles this submission id (saved, or refused as invalid / in
			// conflict). A 401/403/429 or a certificate failure says nothing about an earlier attempt: the flag is kept.
			if (r.IsSuccess || IsEntryAnswer(r.Error!))
			{
				_outcomeUnknown = false;
			}
			else if (r.Error!.OutcomeUnknown)
			{
				_outcomeUnknown = true;
			}
			RememberOutcome(userId, sentId);
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
			case "machine_inactive":
				// The machine was deactivated after the list was loaded: go back to a refreshed machine list (the form is not saved).
				ErrorMessage = e.Message;
				await _dialogs.AlertAsync("Machine no longer active", e.Message);
				await _nav.GoBackAsync();
				break;
			case "submission_payload_mismatch":
				SubmissionId = Guid.NewGuid();
				OnPropertyChanged("SubmissionId");
				ErrorMessage = "An earlier attempt of this entry may already be saved with different details. Check My entries before saving again.";
				break;
			default:
				if (e.Kind == ApiErrorKind.SessionExpired && userId is int uid && _shop is not null && _stage is not null && _machine is not null)
				{
					// The app is about to return to sign-in; keep this entry so it can be resumed with the same submission id.
					_drafts.Save(new EntryDraft(uid, _shop, _stage, _machine, _selected?.Id, QuantityText, Remarks, SubmissionId));
					ErrorMessage = e.Message;
					break;
				}
				ErrorMessage = (e.OutcomeUnknown ? (e.Message + " Your entry may not have been saved. Tap Save again – it will not create a duplicate.") : e.Message);
				break;
			}
		}
		catch (Exception)
		{
			if (!answered)
			{
				_outcomeUnknown = true;
				RememberOutcome(userId, sentId);
			}
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

	/// <summary>A 4xx produced by the entry endpoint itself (it carries the API's problem code), not by a proxy.</summary>
	private static bool IsEntryAnswer(ApiError e)
	{
		return e.Kind is ApiErrorKind.Validation or ApiErrorKind.Conflict && !e.Code.StartsWith("http_", StringComparison.Ordinal);
	}

	/// <summary>Keeps the id of a save with an unknown outcome in the app-wide store (it outlives this form); forgets it once settled.</summary>
	private void RememberOutcome(int? userId, Guid submissionId)
	{
		if (!_outcomeUnknown)
		{
			_drafts.SettlePending(submissionId);
		}
		else if (userId is int uid && _shop is not null && _stage is not null && _machine is not null)
		{
			_drafts.SetPending(new PendingSubmission(uid, _shop.Id, _stage.Id, _machine.Id, submissionId));
		}
	}
}
