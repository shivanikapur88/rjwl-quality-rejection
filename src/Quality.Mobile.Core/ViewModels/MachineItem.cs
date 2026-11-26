// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using Quality.Core.Dtos;

namespace Quality.Mobile.Core.ViewModels;

public sealed class MachineItem
{
	public AdminMachineDto Dto { get; }

	public string Code => Dto.MachineCode;

	public string Name => Dto.MachineName;

	public string ShopName => Dto.ShopName;

	public bool IsActive => Dto.IsActive;

	public string StatusText => Dto.IsActive ? "Active" : "Inactive";

	public string ToggleText => Dto.IsActive ? "Deactivate" : "Activate";

	public MachineItem(AdminMachineDto d)
	{
		Dto = d;
	}
}
