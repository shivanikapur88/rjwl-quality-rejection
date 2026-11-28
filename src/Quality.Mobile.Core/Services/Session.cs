// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Core.Services;

public sealed class Session : ISession
{
	private const string Key = "session.v1";

	private readonly ISecureStore _store;

	private readonly TimeProvider _time;

	public LoginResponse? User { get; private set; }

	public string? Token => IsAuthenticated ? User.Token : null;

	public bool IsAuthenticated => (object)User != null && User.ExpiresAtUtc > _time.GetUtcNow().UtcDateTime.AddMinutes(1.0);

	public bool IsAdministrator => IsAuthenticated && User.Role == "Administrator";

	public bool IsInspector => IsAuthenticated && User.Role == "QualityInspector";

	public bool IsSupervisor => IsAuthenticated && User!.Role == "Supervisor";

	public event Action? Expired;

	public Session(ISecureStore store, TimeProvider time)
	{
		_store = store;
		_time = time;
	}

	public async Task<bool> RestoreAsync()
	{
		try
		{
			string json = await _store.GetAsync("session.v1");
			if (string.IsNullOrEmpty(json))
			{
				return false;
			}
			LoginResponse u = JsonSerializer.Deserialize<LoginResponse>(json);
			if ((object)u == null)
			{
				return false;
			}
			User = u;
			if (IsAuthenticated)
			{
				return true;
			}
			await EndAsync();
			return false;
		}
		catch (Exception)
		{
			User = null;
			return false;
		}
	}

	public async Task StartAsync(LoginResponse login)
	{
		User = login;
		try
		{
			await _store.SetAsync("session.v1", JsonSerializer.Serialize(login));
		}
		catch (Exception)
		{
		}
	}

	public Task EndAsync()
	{
		User = null;
		try
		{
			_store.Remove("session.v1");
		}
		catch (Exception)
		{
		}
		return Task.CompletedTask;
	}

	public void NotifyExpired(string? rejectedToken = null)
	{
		if (rejectedToken != null && User is { } current && current.Token != rejectedToken)
		{
			return;   // review fix: a late 401 for an earlier session must not end a newer one
		}
		bool flag = (object)User != null;
		User = null;
		try
		{
			_store.Remove("session.v1");
		}
		catch (Exception)
		{
		}
		if (flag)
		{
			Expired?.Invoke();
		}
	}
}
