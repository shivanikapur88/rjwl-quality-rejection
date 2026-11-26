// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using Quality.Core.Dtos;

namespace Quality.Mobile.Core.ViewModels;

public sealed class UserItem
{
	public AdminUserDto Dto { get; }

	public string DisplayName => Dto.DisplayName;

	public string Line => Dto.EmployeeCode + " · " + Dto.Username;

	public bool IsActive => Dto.IsActive;

	public string StatusText => Dto.IsActive ? "Active" : "Inactive";

	public string ToggleText => Dto.IsActive ? "Deactivate" : "Activate";

	public string RoleText => UserRoles.Label(Dto.Role);

	public string ShopsText
	{
		get
		{
			string result;
			if (Dto.Role == "Administrator")
			{
				result = "All shops";
			}
			else
			{
				result = ((Dto.ShopCodes.Count == 0) ? "No shops" : string.Join(", ", Dto.ShopCodes));
			}
			return result;
		}
	}

	public UserItem(AdminUserDto d)
	{
		Dto = d;
	}
}
