// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Threading.Tasks;
using Quality.Core.Dtos;

namespace Quality.Mobile.Core.Services;

public interface ISession
{
	string? Token { get; }

	LoginResponse? User { get; }

	bool IsAuthenticated { get; }

	bool IsAdministrator { get; }

	bool IsInspector { get; }

	bool IsSupervisor { get; }

	event Action? Expired;

	Task<bool> RestoreAsync();

	Task StartAsync(LoginResponse login);

	Task EndAsync();

	/// <summary>The server rejected the session. <paramref name="rejectedToken"/>: the token the rejected request carried;
	/// when it is not the current token (a newer sign-in), the current session is kept.</summary>
	void NotifyExpired(string? rejectedToken = null);
}
