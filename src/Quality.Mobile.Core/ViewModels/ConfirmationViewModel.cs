// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Core.ViewModels;

public sealed class ConfirmationViewModel : BaseViewModel
{
	private readonly INavigator _nav;

	private EntryDto? _entry;

	public AsyncCommand RecordAnotherCommand { get; }

	public AsyncCommand HomeCommand { get; }

	public string Headline { get; private set; } = "";

	public string EntryNumber => ((object)_entry == null) ? "" : $"Entry #{_entry.Id}";

	public string Shop => (_entry?.ShopCode == null) ? "" : (_entry.ShopCode ?? "");

	public string Stage => _entry?.StageName ?? "";

	public string Machine => ((object)_entry == null) ? "" : (_entry.MachineCode + " – " + _entry.MachineName);

	public string Phenomenon => _entry?.PhenomenonName ?? "";

	public string Quantity => ((object)_entry == null) ? "" : _entry.Quantity.ToString();

	public string Inspector => _entry?.InspectorEmployeeCode ?? "";

	public string SavedAt => ((object)_entry == null) ? "" : _entry.ServerCreatedAtUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss");

	public string? Remarks => _entry?.Remarks;

	public bool HasRemarks => !string.IsNullOrWhiteSpace(_entry?.Remarks);

	public ConfirmationViewModel(INavigator nav)
	{
		_nav = nav;
		Title = "Entry saved";
		RecordAnotherCommand = new AsyncCommand(() => _nav.GoBackAsync());
		HomeCommand = new AsyncCommand(() => _nav.GoToAsync("//home"));
	}

	public override Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		_entry = BaseViewModel.Arg<EntryDto>(args, "entry");
		bool flag = args.TryGetValue("already", out object value) && value is bool && (bool)value;
		Headline = (flag ? "Already saved earlier – no duplicate created" : "Rejection entry saved");
		string[] array = new string[11]
		{
			"Headline", "EntryNumber", "Shop", "Stage", "Machine", "Phenomenon", "Quantity", "Inspector", "SavedAt", "Remarks",
			"HasRemarks"
		};
		foreach (string name in array)
		{
			OnPropertyChanged(name);
		}
		return Task.CompletedTask;
	}
}
