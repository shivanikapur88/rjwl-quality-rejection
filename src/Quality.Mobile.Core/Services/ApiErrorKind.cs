// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
namespace Quality.Mobile.Core.Services;

public enum ApiErrorKind
{
	NotConfigured,
	Network,
	Timeout,
	Unauthorized,
	SessionExpired,
	Forbidden,
	NotFound,
	Validation,
	Conflict,
	TooManyRequests,
	Server
}
