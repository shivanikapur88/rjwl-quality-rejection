// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;

namespace Quality.Mobile.Core.ViewModels;

public sealed class UserEditorViewModel : BaseViewModel
{
	private readonly IApiClient _api;

	private readonly INavigator _nav;

	private AdminUserDto? _editing;

	private string _emp = "";

	private string _user = "";

	private string _display = "";

	private string _pw = "";

	private string _pw2 = "";

	private RoleOption _role = UserRoles.All[0];

	/// <summary>Review fix: the profile values already saved by this screen (the password step then failed),
	/// so a retry does not send the update again.</summary>
	private string? _savedProfile;

	public bool IsEdit => (object)_editing != null;

	public bool IsCreate => (object)_editing == null;

	public string FixedIdentity => ((object)_editing == null) ? "" : (_editing.EmployeeCode + " · " + _editing.Username);

	public string EmployeeCode
	{
		get
		{
			return _emp;
		}
		set
		{
			Set(ref _emp, value, "EmployeeCode");
		}
	}

	public string Username
	{
		get
		{
			return _user;
		}
		set
		{
			Set(ref _user, value, "Username");
		}
	}

	public string DisplayName
	{
		get
		{
			return _display;
		}
		set
		{
			Set(ref _display, value, "DisplayName");
		}
	}

	public IReadOnlyList<RoleOption> Roles => UserRoles.All;

	public RoleOption SelectedRole
	{
		get
		{
			return _role;
		}
		set
		{
			if ((object)value != null && Set(ref _role, value, "SelectedRole"))
			{
				OnPropertyChanged("ShowShops");
			}
		}
	}

	public bool ShowShops => _role.Value != "Administrator";

	public ObservableCollection<ShopChoice> ShopChoices { get; } = new ObservableCollection<ShopChoice>();

	public string Password
	{
		get
		{
			return _pw;
		}
		set
		{
			Set(ref _pw, value, "Password");
		}
	}

	public string ConfirmPassword
	{
		get
		{
			return _pw2;
		}
		set
		{
			Set(ref _pw2, value, "ConfirmPassword");
		}
	}

	public string PasswordLabel => IsEdit ? "New password (leave blank to keep the current one)" : "Password";

	public string PasswordRule => $"At least {10} characters";

	public AsyncCommand SaveCommand { get; }

	public AsyncCommand CancelCommand { get; }

	public UserEditorViewModel(IApiClient api, INavigator nav)
	{
		_api = api;
		_nav = nav;
		SaveCommand = new AsyncCommand(SaveAsync);
		CancelCommand = new AsyncCommand(() => _nav.GoBackAsync());
	}

	public override async Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		_editing = BaseViewModel.Arg<AdminUserDto>(args, "editUser");
		_savedProfile = null;
		Title = (IsEdit ? "Edit user" : "Add user");
		string[] array = new string[4] { "IsEdit", "IsCreate", "FixedIdentity", "PasswordLabel" };
		foreach (string n in array)
		{
			OnPropertyChanged(n);
		}
		if ((object)_editing != null)
		{
			DisplayName = _editing.DisplayName;
			SelectedRole = UserRoles.All.First((RoleOption r) => r.Value == _editing.Role);
		}
		await RunBusyAsync(async () =>
		{
			ApiResult<List<ShopDto>> r = await _api.GetShopsAsync();
			if (!r.IsSuccess)
			{
				ErrorMessage = r.Error.Message;
			}
			else
			{
				ShopChoices.Clear();
				foreach (ShopDto s in r.Value)
				{
					ShopChoices.Add(new ShopChoice(s, _editing?.ShopIds.Contains(s.Id) ?? false));
				}
			}
		});
	}

	public async Task SaveAsync()
	{
		ErrorMessage = null;
		string emp = (EmployeeCode ?? "").Trim();
		string user = (Username ?? "").Trim();
		string display = (DisplayName ?? "").Trim();
		string pw = Password ?? "";
		string pw2 = ConfirmPassword ?? "";
		if (IsCreate && (emp.Length == 0 || user.Length == 0))
		{
			ErrorMessage = "Enter the employee code and the username.";
			return;
		}
		if (display.Length == 0)
		{
			ErrorMessage = "Enter the user's name.";
			return;
		}
		List<int> shopIds = (from c in ShopChoices
			where c.IsSelected
			select c.Shop.Id).ToList();
		if (ShowShops && shopIds.Count == 0)
		{
			ErrorMessage = "Select at least one shop for this user.";
			return;
		}
		bool wantsPassword = IsCreate || pw.Length > 0;
		if (wantsPassword)
		{
			if (pw != pw2)
			{
				ErrorMessage = "The two passwords do not match.";
				return;
			}
			if (pw.Length < 10)
			{
				ErrorMessage = PasswordRule + ".";
				return;
			}
		}
		string role = SelectedRole.Value;
		List<int> shops = (ShowShops ? shopIds : new List<int>());
		await RunBusyAsync(async () =>
		{
			if (IsCreate)
			{
				ApiResult<AdminUserDto> r = await _api.AdminUserCreateAsync(new CreateUserRequest(emp, user, display, role, pw, shops));
				if (!r.IsSuccess)
				{
					ErrorMessage = r.Error.Message;
					return;
				}
			}
			else
			{
				string profile = display + "\n" + role + "\n" + string.Join(",", shops);
				if (profile != _savedProfile)
				{
					ApiResult<AdminUserDto> u = await _api.AdminUserUpdateAsync(_editing.Id, new UpdateUserRequest(display, role, shops, _editing.Version));
					if (!u.IsSuccess)
					{
						ErrorMessage = u.Error.Message;
						return;
					}
					// Review fix: the record now has a new version; a later save from this screen must send it.
					_editing = u.Value ?? _editing;
					_savedProfile = profile;
				}
				if (wantsPassword)
				{
					ApiResult<Unit> p = await _api.AdminUserResetPasswordAsync(_editing!.Id, new ResetPasswordRequest(pw));
					if (!p.IsSuccess)
					{
						ErrorMessage = "The user was updated, but the password was not changed: " + p.Error.Message;
						return;
					}
				}
			}
			Password = "";
			ConfirmPassword = "";
			await _nav.GoBackAsync();
		});
	}
}
