// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Quality.Mobile.Core.Infrastructure;

public interface INavigator
{
	Task GoToAsync(string route, IDictionary<string, object>? args = null);

	Task GoBackAsync();
}
