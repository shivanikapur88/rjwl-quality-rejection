// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Threading.Tasks;

namespace Quality.Mobile.Core.Infrastructure;

public interface IDialogs
{
	Task AlertAsync(string title, string message);

	Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}
