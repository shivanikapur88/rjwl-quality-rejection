// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
namespace Quality.Mobile.Core.Services;

public sealed record ApiError(ApiErrorKind Kind, string Code, string Message, int? StatusCode = null)
{
	public bool OutcomeUnknown
	{
		get
		{
			ApiErrorKind kind = Kind;
			// Review fix: a TLS/certificate failure or an unusable address fails before anything is sent.
			if (Code is "certificate_untrusted" or "invalid_server_address" or "not_configured")
			{
				return false;
			}
			if ((uint)(kind - 1) <= 1u || kind == ApiErrorKind.Server)
			{
				return true;
			}
			return false;
		}
	}
}
