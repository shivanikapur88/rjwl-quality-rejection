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

	private bool _isRefreshing;

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
				OnBusyChanged();
			}
		}
	}

	public bool IsNotBusy => !_isBusy;

	/// <summary>Called when IsBusy changes: commands whose CanExecute depends on IsBusy re-evaluate here (otherwise a button
	/// disabled while busy - e.g. Save after a restored draft - stays disabled until something else changes).</summary>
	protected virtual void OnBusyChanged() { }

	/// <summary>Review fix: bound (TwoWay) to a RefreshView's IsRefreshing. RefreshView runs its command whenever IsRefreshing
	/// becomes true, even when set from code, so only the pull-to-refresh path (<see cref="PullToRefreshAsync"/>) sets it;
	/// other loads (first load, "Load more", day change) show progress through IsBusy and never start a refresh.</summary>
	public bool IsRefreshing
	{
		get
		{
			return _isRefreshing;
		}
		set
		{
			Set(ref _isRefreshing, value, "IsRefreshing");
		}
	}

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

	/// <summary>The pull-to-refresh command: runs <paramref name="load"/> and always clears IsRefreshing afterwards.</summary>
	protected async Task PullToRefreshAsync(Func<Task> load)
	{
		IsRefreshing = true;
		try
		{
			await load();
		}
		finally
		{
			IsRefreshing = false;
		}
	}

	protected static T? Arg<T>(IDictionary<string, object> args, string key) where T : class
	{
		object value;
		return args.TryGetValue(key, out value) ? (value as T) : null;
	}
}
