// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class MyEntriesViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly ISession _session;

	private bool _empty;

	public ObservableCollection<EntryRow> Entries { get; } = new ObservableCollection<EntryRow>();

	public AsyncCommand RefreshCommand { get; }

	public bool IsEmpty
	{
		get
		{
			return _empty;
		}
		private set
		{
			Set(ref _empty, value, "IsEmpty");
		}
	}

	public string Heading => _session.IsInspector ? "My recent entries" : "Recent entries";

	public MyEntriesViewModel(IApiClient api, ISession session)
	{
		_api = api;
		_session = session;
		RefreshCommand = new AsyncCommand(LoadAsync);
	}

	public override Task OnAppearingAsync()
	{
		OnPropertyChanged("Heading");
		return LoadAsync();
	}

	public Task LoadAsync()
	{
		return RunBusyAsync(async () =>
		{
			ApiResult<List<EntryDto>> r = await _api.GetEntriesAsync();
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				Entries.Clear();
				foreach (EntryDto e in r.Value)
				{
					Entries.Add(new EntryRow(e.Id, $"{e.PhenomenonName} × {e.Quantity}", $"{e.ShopCode} › {e.StageName} › {e.MachineCode}", $"{e.ServerCreatedAtUtc.ToLocalTime():dd MMM HH:mm} · {e.InspectorEmployeeCode}" + (string.IsNullOrWhiteSpace(e.Remarks) ? "" : (" · " + e.Remarks))));
				}
				IsEmpty = Entries.Count == 0;
			}
		});
	}
}
