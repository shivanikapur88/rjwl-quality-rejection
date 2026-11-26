// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
namespace Quality.Mobile.Core.ViewModels;

public sealed record RoleOption(string Value, string Label)
{
	public override string ToString()
	{
		return Label;
	}
}
