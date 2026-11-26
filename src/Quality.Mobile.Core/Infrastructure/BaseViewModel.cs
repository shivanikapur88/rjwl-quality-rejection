// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Quality.Mobile.Core.Infrastructure;

public abstract class BaseViewModel : ObservableObject, INavigationAware
{
	private bool _isBusy;

	private string? _error;

	private string _title = "";

	public const string GenericError = "Something went wrong. Please try again.";

	public bool IsBusy
	{
		get
		{
			return _isBusy;
		}
		protected set
		{
			if (Set(ref _isBusy, value, "IsBusy"))
			{
				OnPropertyChanged("IsNotBusy");
			}
		}
	}

	public bool IsNotBusy => !_isBusy;

	public string Title
	{
		get
		{
			return _title;
		}
		protected set
		{
			Set(ref _title, value, "Title");
		}
	}

	public string? ErrorMessage
	{
		get
		{
			return _error;
		}
		protected set
		{
			if (Set(ref _error, value, "ErrorMessage"))
			{
				OnPropertyChanged("HasError");
			}
		}
	}

	public bool HasError => !string.IsNullOrEmpty(_error);

	public virtual Task OnNavigatedToAsync(IDictionary<string, object> args)
	{
		return Task.CompletedTask;
	}

	public virtual Task OnAppearingAsync()
	{
		return Task.CompletedTask;
	}

	protected async Task RunBusyAsync(Func<Task> work)
	{
		IsBusy = true;
		ErrorMessage = null;
		try
		{
			await work();
		}
		catch (Exception)
		{
			ErrorMessage = "Something went wrong. Please try again.";
		}
		finally
		{
			IsBusy = false;
		}
	}

	protected static T? Arg<T>(IDictionary<string, object> args, string key) where T : class
	{
		object value;
		return args.TryGetValue(key, out value) ? (value as T) : null;
	}
}
