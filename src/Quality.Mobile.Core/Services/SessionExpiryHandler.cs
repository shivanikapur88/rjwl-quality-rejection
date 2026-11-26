// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Threading.Tasks;
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Core.Services;

public sealed class SessionExpiryHandler
{
	private readonly INavigator _nav;

	private readonly IDialogs _dialogs;

	public SessionExpiryHandler(ISession session, INavigator nav, IDialogs dialogs)
	{
		_nav = nav;
		_dialogs = dialogs;
		session.Expired += () =>
		{
			HandleAsync();
		};
	}

	private async Task HandleAsync()
	{
		try
		{
			await _nav.GoToAsync("//login");
			await _dialogs.AlertAsync("Session expired", "Please sign in again.");
		}
		catch (Exception)
		{
		}
	}
}
