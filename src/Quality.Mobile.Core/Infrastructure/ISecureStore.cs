// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Threading.Tasks;

namespace Quality.Mobile.Core.Infrastructure;

public interface ISecureStore
{
	Task<string?> GetAsync(string key);

	Task SetAsync(string key, string value);

	void Remove(string key);
}
