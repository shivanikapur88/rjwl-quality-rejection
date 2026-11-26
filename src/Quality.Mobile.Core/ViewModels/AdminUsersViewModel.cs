// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class AdminUsersViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private readonly IDialogs _dialogs;

	private Option? _status;

	private string _search = "";

	private int _version;

	public List<Option> StatusOptions { get; }

	public ObservableCollection<UserItem> Items { get; } = new ObservableCollection<UserItem>();

	public string CountText => $"{Items.Count} shown";

	public Option? SelectedStatus
	{
		get
		{
			return _status;
		}
		set
		{
			if (Set(ref _status, value, "SelectedStatus"))
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

	public AsyncCommand<UserItem> EditCommand { get; }

	public AsyncCommand<UserItem> ToggleCommand { get; }

	public AdminUsersViewModel(IApiClient api, INavigator nav, IDialogs dialogs)
	{
		_api = api;
		_nav = nav;
		_dialogs = dialogs;
		Title = "Users";
		StatusOptions = new List<Option>
		{
			new Option(null, "All"),
			new Option(1, "Active"),
			new Option(0, "Inactive")
		};
		_status = StatusOptions[0];
		SearchCommand = new AsyncCommand(LoadAsync);
		AddCommand = new AsyncCommand(() => _nav.GoToAsync("usereditor"));
		EditCommand = new AsyncCommand<UserItem>((UserItem i) => _nav.GoToAsync("usereditor", new Dictionary<string, object> { ["editUser"] = i.Dto }));
		ToggleCommand = new AsyncCommand<UserItem>(ToggleAsync);
	}

	public override Task OnAppearingAsync()
	{
		return LoadAsync();
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
			ApiResult<List<AdminUserDto>> r = await _api.AdminUsersListAsync(_search, active);
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
			foreach (AdminUserDto d in r.Value)
			{
				Items.Add(new UserItem(d));
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

	public async Task ToggleAsync(UserItem item)
	{
		bool isActive = item.IsActive;
		bool flag = isActive;
		if (flag)
		{
			flag = !(await _dialogs.ConfirmAsync("Deactivate user", $"Deactivate {item.DisplayName} ({item.Dto.EmployeeCode})?\nThey will not be able to sign in. Their past entries are kept.", "Deactivate", "Cancel"));
		}
		if (flag)
		{
			return;
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<AdminUserDto> r = await _api.AdminUserSetStatusAsync(item.Dto.Id, !item.IsActive, item.Dto.Version);
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				int idx = Items.IndexOf(item);
				if (idx >= 0)
				{
					Items[idx] = new UserItem(r.Value);
				}
			}
		});
	}
}
