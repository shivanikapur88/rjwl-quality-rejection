// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
namespace Quality.Mobile.Core.Services;

public sealed record ApiError(ApiErrorKind Kind, string Code, string Message, int? StatusCode = null)
{
	public bool OutcomeUnknown
	{
		get
		{
			ApiErrorKind kind = Kind;
			if ((uint)(kind - 1) <= 1u || kind == ApiErrorKind.Server)
			{
				return true;
			}
			return false;
		}
	}
}
