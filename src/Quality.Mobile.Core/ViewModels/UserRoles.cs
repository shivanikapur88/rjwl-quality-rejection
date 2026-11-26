// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Linq;

namespace Quality.Mobile.Core.ViewModels;

public static class UserRoles
{
	public static readonly IReadOnlyList<RoleOption> All = new RoleOption[3]
	{
		new RoleOption("QualityInspector", "Quality Inspector"),
		new RoleOption("Supervisor", "Supervisor"),
		new RoleOption("Administrator", "Administrator")
	};

	public static string Label(string role)
	{
		return All.FirstOrDefault((RoleOption r) => r.Value == role)?.Label ?? role;
	}
}
