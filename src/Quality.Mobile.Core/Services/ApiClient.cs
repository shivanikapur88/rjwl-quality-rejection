// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Core.Services;

public sealed class ApiClient : IApiClient
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20.0);

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly HttpClient _http;

	private readonly IAppSettings _settings;

	private readonly ISession _session;

	private readonly TimeProvider _time;

	public ApiClient(HttpClient http, IAppSettings settings, ISession session, TimeProvider? time = null)
	{
		_http = http;
		_settings = settings;
		_session = session;
		_time = time ?? TimeProvider.System;
		_http.Timeout = Timeout;
	}

	public Task<ApiResult<Unit>> HealthAsync()
	{
		return SendAsync<Unit>(HttpMethod.Get, "/api/health", null, auth: false);
	}

	/// <summary>Review fix: checks a candidate server address (Settings, "Test connection") without saving it. No token is sent.</summary>
	public Task<ApiResult<Unit>> TestConnectionAsync(string baseUrl)
	{
		return SendAsync<Unit>(HttpMethod.Get, "/api/health", null, auth: false, baseUrlOverride: baseUrl ?? "");
	}

	public Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest r)
	{
		return SendAsync<LoginResponse>(HttpMethod.Post, "/api/auth/login", r, auth: false, adjust: ToDeviceClock);
	}

	/// <summary>Review fix: the expiry is in server time. When the device clock differs (server Date header), it is moved
	/// onto the device clock, so a tablet clock hours ahead does not make a new session look expired (or one behind, valid too long).</summary>
	private LoginResponse ToDeviceClock(LoginResponse login, HttpResponseMessage resp)
	{
		if (resp.Headers.Date is not DateTimeOffset serverNow)
		{
			return login;   // no Date header: assume the device clock is right
		}
		TimeSpan skew = serverNow.UtcDateTime - _time.GetUtcNow().UtcDateTime;
		return skew.Duration() < TimeSpan.FromSeconds(30.0) ? login : login with { ExpiresAtUtc = login.ExpiresAtUtc - skew };
	}

	public Task<ApiResult<List<ShopDto>>> GetShopsAsync()
	{
		return SendAsync<List<ShopDto>>(HttpMethod.Get, "/api/shops");
	}

	public Task<ApiResult<List<StageDto>>> GetStagesAsync(int shopId)
	{
		return SendAsync<List<StageDto>>(HttpMethod.Get, $"/api/shops/{shopId}/stages");
	}

	public Task<ApiResult<List<MachineDto>>> GetMachinesAsync(int shopId, int stageId)
	{
		return SendAsync<List<MachineDto>>(HttpMethod.Get, $"/api/shops/{shopId}/stages/{stageId}/machines");
	}

	public Task<ApiResult<List<PhenomenonDto>>> GetPhenomenaAsync(int shopId, int stageId)
	{
		return SendAsync<List<PhenomenonDto>>(HttpMethod.Get, $"/api/shops/{shopId}/stages/{stageId}/phenomena");
	}

	public Task<ApiResult<CreateEntryResult>> SubmitEntryAsync(CreateEntryRequest r)
	{
		return SendAsync<CreateEntryResult>(HttpMethod.Post, "/api/rejection-entries", r);
	}

	public Task<ApiResult<List<EntryDto>>> GetEntriesAsync(int limit = 50)
	{
		return SendAsync<List<EntryDto>>(HttpMethod.Get, $"/api/rejection-entries?limit={limit}");
	}

	public Task<ApiResult<EntryDto>> GetEntryAsync(int shopId, long id) => SendAsync<EntryDto>(HttpMethod.Get, $"/api/rejection-entries/{shopId}/{id}");

	public Task<ApiResult<CorrectionDto>> RequestCorrectionAsync(int shopId, long entryId, CorrectionRequest request) =>
		SendAsync<CorrectionDto>(HttpMethod.Post, $"/api/rejection-entries/{shopId}/{entryId}/corrections", request);

	public Task<ApiResult<List<CorrectionDto>>> GetCorrectionsAsync(string status = "Pending") =>
		SendAsync<List<CorrectionDto>>(HttpMethod.Get, "/api/corrections?status=" + Uri.EscapeDataString(status));

	public Task<ApiResult<CorrectionDto>> ApproveCorrectionAsync(long id, string? note) =>
		SendAsync<CorrectionDto>(HttpMethod.Post, $"/api/corrections/{id}/approve", new CorrectionDecisionRequest(note));

	public Task<ApiResult<CorrectionDto>> RejectCorrectionAsync(long id, string note) =>
		SendAsync<CorrectionDto>(HttpMethod.Post, $"/api/corrections/{id}/reject", new CorrectionDecisionRequest(note));

	public Task<ApiResult<CorrectionDto>> WithdrawCorrectionAsync(long id) => SendAsync<CorrectionDto>(HttpMethod.Post, $"/api/corrections/{id}/withdraw");

	public Task<ApiResult<CalendarDto>> GetCalendarAsync() => SendAsync<CalendarDto>(HttpMethod.Get, "/api/reports/calendar");

	public Task<ApiResult<ReportDto>> GetTotalsAsync(DateOnly fromDate, DateOnly toDate, string? shiftCode, int top = 5)
	{
		var path = "/api/reports/totals?fromDate=" + fromDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "&toDate=" + toDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "&top=" + top.ToString(System.Globalization.CultureInfo.InvariantCulture);
		if (!string.IsNullOrEmpty(shiftCode)) path += "&shiftCode=" + Uri.EscapeDataString(shiftCode);
		return SendAsync<ReportDto>(HttpMethod.Get, path);
	}

	public Task<ApiResult<MachineLookupDto>> LookupMachineAsync(string scannedText) =>
		SendAsync<MachineLookupDto>(HttpMethod.Get, "/api/machines/lookup?code=" + Uri.EscapeDataString(scannedText ?? ""));

	public Task<ApiResult<EntryPage>> GetEntriesPageAsync(string? cursor, int limit = 50)
	{
		var path = $"/api/rejection-entries/page?limit={limit}";
		if (!string.IsNullOrEmpty(cursor)) path += "&cursor=" + Uri.EscapeDataString(cursor);
		return SendAsync<EntryPage>(HttpMethod.Get, path);
	}

	public Task<ApiResult<List<AdminPhenomenonDto>>> AdminListAsync(int? shopId, int? stageId, bool? isActive, string? search)
	{
		List<string> list = new List<string>();
		if (shopId.HasValue)
		{
			int valueOrDefault = shopId.GetValueOrDefault();
			if (true)
			{
				list.Add($"shopId={valueOrDefault}");
			}
		}
		if (stageId.HasValue)
		{
			int valueOrDefault2 = stageId.GetValueOrDefault();
			if (true)
			{
				list.Add($"stageId={valueOrDefault2}");
			}
		}
		if (isActive.HasValue)
		{
			bool valueOrDefault3 = isActive == true;
			if (true)
			{
				list.Add("isActive=" + (valueOrDefault3 ? "true" : "false"));
			}
		}
		if (!string.IsNullOrWhiteSpace(search))
		{
			list.Add("search=" + Uri.EscapeDataString(search.Trim()));
		}
		return SendAsync<List<AdminPhenomenonDto>>(HttpMethod.Get, "/api/admin/phenomena" + ((list.Count > 0) ? ("?" + string.Join("&", list)) : ""));
	}

	public Task<ApiResult<AdminPhenomenonDto>> AdminCreateAsync(CreatePhenomenonRequest r)
	{
		return SendAsync<AdminPhenomenonDto>(HttpMethod.Post, "/api/admin/phenomena", r);
	}

	public Task<ApiResult<AdminPhenomenonDto>> AdminUpdateAsync(int id, UpdatePhenomenonRequest r)
	{
		return SendAsync<AdminPhenomenonDto>(HttpMethod.Put, $"/api/admin/phenomena/{id}", r);
	}

	public Task<ApiResult<AdminPhenomenonDto>> AdminSetStatusAsync(int id, bool isActive, string? version = null)
	{
		return SendAsync<AdminPhenomenonDto>(HttpMethod.Patch, $"/api/admin/phenomena/{id}/status", new SetStatusRequest(isActive, version));
	}

	public Task<ApiResult<Unit>> AdminDeleteAsync(int id)
	{
		return SendAsync<Unit>(HttpMethod.Delete, $"/api/admin/phenomena/{id}");
	}

	public Task<ApiResult<List<AdminMachineDto>>> AdminMachinesListAsync(int? shopId, bool? isActive, string? search)
	{
		List<string> list = new List<string>();
		if (shopId.HasValue)
		{
			int valueOrDefault = shopId.GetValueOrDefault();
			if (true)
			{
				list.Add($"shopId={valueOrDefault}");
			}
		}
		if (isActive.HasValue)
		{
			bool valueOrDefault2 = isActive == true;
			if (true)
			{
				list.Add("isActive=" + (valueOrDefault2 ? "true" : "false"));
			}
		}
		if (!string.IsNullOrWhiteSpace(search))
		{
			list.Add("search=" + Uri.EscapeDataString(search.Trim()));
		}
		return SendAsync<List<AdminMachineDto>>(HttpMethod.Get, "/api/admin/machines" + ((list.Count > 0) ? ("?" + string.Join("&", list)) : ""));
	}

	public Task<ApiResult<AdminMachineDto>> AdminMachineCreateAsync(CreateMachineRequest r)
	{
		return SendAsync<AdminMachineDto>(HttpMethod.Post, "/api/admin/machines", r);
	}

	public Task<ApiResult<AdminMachineDto>> AdminMachineUpdateAsync(int id, UpdateMachineRequest r)
	{
		return SendAsync<AdminMachineDto>(HttpMethod.Put, $"/api/admin/machines/{id}", r);
	}

	public Task<ApiResult<AdminMachineDto>> AdminMachineSetStatusAsync(int id, bool isActive, string? version = null)
	{
		return SendAsync<AdminMachineDto>(HttpMethod.Patch, $"/api/admin/machines/{id}/status", new SetStatusRequest(isActive, version));
	}

	public Task<ApiResult<Unit>> AdminMachineDeleteAsync(int id)
	{
		return SendAsync<Unit>(HttpMethod.Delete, $"/api/admin/machines/{id}");
	}

	public Task<ApiResult<List<AdminUserDto>>> AdminUsersListAsync(string? search, bool? isActive)
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(search))
		{
			list.Add("search=" + Uri.EscapeDataString(search.Trim()));
		}
		if (isActive.HasValue)
		{
			bool valueOrDefault = isActive == true;
			if (true)
			{
				list.Add("isActive=" + (valueOrDefault ? "true" : "false"));
			}
		}
		return SendAsync<List<AdminUserDto>>(HttpMethod.Get, "/api/admin/users" + ((list.Count > 0) ? ("?" + string.Join("&", list)) : ""));
	}

	public Task<ApiResult<AdminUserDto>> AdminUserCreateAsync(CreateUserRequest r)
	{
		return SendAsync<AdminUserDto>(HttpMethod.Post, "/api/admin/users", r);
	}

	public Task<ApiResult<AdminUserDto>> AdminUserUpdateAsync(int id, UpdateUserRequest r)
	{
		return SendAsync<AdminUserDto>(HttpMethod.Put, $"/api/admin/users/{id}", r);
	}

	public Task<ApiResult<AdminUserDto>> AdminUserSetStatusAsync(int id, bool isActive, string? version = null)
	{
		return SendAsync<AdminUserDto>(HttpMethod.Patch, $"/api/admin/users/{id}/status", new SetStatusRequest(isActive, version));
	}

	public Task<ApiResult<Unit>> AdminUserResetPasswordAsync(int id, ResetPasswordRequest r)
	{
		return SendAsync<Unit>(HttpMethod.Post, $"/api/admin/users/{id}/reset-password", r);
	}

	private static ApiResult<T> Fail<T>(ApiErrorKind k, string code, string msg, int? status = null)
	{
		return ApiResult<T>.Fail(new ApiError(k, code, msg, status));
	}

	private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null, bool auth = true, Func<T, HttpResponseMessage, T>? adjust = null, string? baseUrlOverride = null)
	{
		string baseUrl = (baseUrlOverride ?? _settings.BaseUrl)?.Trim().TrimEnd('/');
		if (string.IsNullOrEmpty(baseUrl))
		{
			return Fail<T>(ApiErrorKind.NotConfigured, "not_configured", "The server address is not set. Open Settings and enter it.");
		}
		// The stored address may predate the https-only rule (e.g. an old http:// value): never send the token to it.
		if (!BaseUrlValidator.TryNormalize(baseUrl, out string validUrl, out string urlError))
		{
			return Fail<T>(ApiErrorKind.NotConfigured, "invalid_server_address", (baseUrlOverride == null ? "The saved server address is not valid (" : "The server address is not valid (") + urlError.TrimEnd('.') + ")." + (baseUrlOverride == null ? " Open Settings and correct it." : ""));
		}
		baseUrl = validUrl;
		using HttpRequestMessage req = new HttpRequestMessage(method, baseUrl + path);
		string? token = null;
		if (auth)
		{
			token = _session.Token;
			if (token == null)
			{
				_session.NotifyExpired();
				return Fail<T>(ApiErrorKind.SessionExpired, "token_expired", "Your session has expired. Please sign in again.");
			}
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}
		if (body != null)
		{
			req.Content = JsonContent.Create(body, body.GetType(), null, Json);
		}
		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req);
		}
		catch (TaskCanceledException)
		{
			return Fail<T>(ApiErrorKind.Timeout, "timeout", "The server took too long to respond. Check your connection and try again.");
		}
		catch (Exception ex) when (IsCertificateFailure(ex))   // any type: Android also raises WebException / IOException
		{
			return Fail<T>(ApiErrorKind.Network, "certificate_untrusted", CertificateMessage);
		}
		catch (HttpRequestException)
		{
			return Fail<T>(ApiErrorKind.Network, "network", "Cannot reach the server. Check your Wi-Fi or mobile data and the server address, then try again.");
		}
		catch (Exception)
		{
			return Fail<T>(ApiErrorKind.Network, "network", "Cannot reach the server. Check your connection and try again.");
		}
		using (resp)
		{
			if (resp.IsSuccessStatusCode)
			{
				if (typeof(T) == typeof(Unit))
				{
					return ApiResult<T>.Ok(default);
				}
				try
				{
					T v = await resp.Content.ReadFromJsonAsync<T>(Json);
					return (v == null) ? Fail<T>(ApiErrorKind.Server, "bad_response", "The server sent an unexpected response.", (int)resp.StatusCode) : ApiResult<T>.Ok(adjust == null ? v : adjust(v, resp));
				}
				catch (Exception)
				{
					return Fail<T>(ApiErrorKind.Server, "bad_response", "The server sent an unexpected response.", (int)resp.StatusCode);
				}
			}
			return await MapFailureAsync<T>(resp, auth, token);
		}
	}

	public const string CertificateMessage = "The server's security certificate is not trusted on this device. Contact IT.";

	/// <summary>Review fix: a certificate TRUST failure only, found anywhere in the exception chain (of any exception type):
	/// .NET's AuthenticationException, Android's CertPathValidatorException / CertificateException / SSLPeerUnverifiedException,
	/// or an SSLHandshakeException (or wrapper) whose text names the certificate or trust anchor. A handshake that timed out
	/// or was reset ("SSL handshake aborted", SecureConnectionError with an EOF) stays a network error: weak Wi-Fi, not IT.</summary>
	internal static bool IsCertificateFailure(Exception ex)
	{
		for (Exception? e = ex; e != null; e = e.InnerException)
		{
			if (e is System.Security.Authentication.AuthenticationException)
			{
				return true;
			}
			string type = e.GetType().Name;
			if (type is "CertPathValidatorException" or "CertificateException" or "CertificateExpiredException"
				or "CertificateNotYetValidException" or "SSLPeerUnverifiedException")
			{
				return true;
			}
			string m = e.Message ?? "";
			if (m.Contains("certificate", StringComparison.OrdinalIgnoreCase) || m.Contains("trust anchor", StringComparison.OrdinalIgnoreCase)
				|| m.Contains("CertPath", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private async Task<ApiResult<T>> MapFailureAsync<T>(HttpResponseMessage resp, bool auth, string? token)
	{
		string code = null;
		string title = null;
		try
		{
			MediaTypeHeaderValue? contentType = resp.Content.Headers.ContentType;
			if (contentType != null && contentType.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
			{
				using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
				if (doc.RootElement.ValueKind == JsonValueKind.Object)
				{
					if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
					{
						code = c.GetString();
					}
					if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
					{
						title = t.GetString();
					}
				}
			}
		}
		catch (Exception)
		{
		}
		int status = (int)resp.StatusCode;
		if (code == null)
		{
			code = "http_" + status;
		}
		switch (resp.StatusCode)
		{
		case HttpStatusCode.Unauthorized:
			if (auth)
			{
				_session.NotifyExpired(token);   // ignored when a newer session replaced the one this request used
				return Fail<T>(ApiErrorKind.SessionExpired, code, "Your session has expired. Please sign in again.", status);
			}
			return Fail<T>(ApiErrorKind.Unauthorized, code, title ?? "Invalid username or password.", status);
		case HttpStatusCode.Forbidden:
			return Fail<T>(ApiErrorKind.Forbidden, code, title ?? "You do not have permission to do this.", status);
		case HttpStatusCode.NotFound:
			return Fail<T>(ApiErrorKind.NotFound, code, title ?? "Not found.", status);
		case HttpStatusCode.BadRequest:
			return Fail<T>(ApiErrorKind.Validation, code, title ?? "The request was not valid.", status);
		case HttpStatusCode.Conflict:
			return Fail<T>(ApiErrorKind.Conflict, code, title ?? "This conflicts with existing data.", status);
		case HttpStatusCode.TooManyRequests:
			return Fail<T>(ApiErrorKind.TooManyRequests, code, "Too many attempts. Wait a minute and try again.", status);
		default:
		{
			string msg = ((status == 503 && title != null) ? title : "The server had a problem. Please try again shortly.");
			return Fail<T>(ApiErrorKind.Server, code, msg, status);
		}
		}
	}
}
