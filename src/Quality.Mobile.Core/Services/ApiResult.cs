// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
namespace Quality.Mobile.Core.Services;

public sealed class ApiResult<T>
{
	public T? Value { get; }

	public ApiError? Error { get; }

	public bool IsSuccess => (object)Error == null;

	private ApiResult(T? value, ApiError? error)
	{
		Value = value;
		Error = error;
	}

	public static ApiResult<T> Ok(T value)
	{
		return new ApiResult<T>(value, null);
	}

	public static ApiResult<T> Fail(ApiError e)
	{
		return new ApiResult<T>(default, e);
	}
}
