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

	event Action? Expired;

	Task<bool> RestoreAsync();

	Task StartAsync(LoginResponse login);

	Task EndAsync();

	void NotifyExpired();
}
