// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Quality.Mobile.Core.Infrastructure;

public sealed class AsyncCommand : ICommand
{
	private readonly Func<Task> _execute;

	private readonly Func<bool>? _canExecute;

	private bool _running;

	public event EventHandler? CanExecuteChanged;

	public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
	{
		_execute = execute;
		_canExecute = canExecute;
	}

	public bool CanExecute(object? parameter)
	{
		return !_running && (_canExecute?.Invoke() ?? true);
	}

	public async void Execute(object? parameter)
	{
		await ExecuteAsync();
	}

	public async Task ExecuteAsync()
	{
		if (!CanExecute(null))
		{
			return;
		}
		_running = true;
		RaiseCanExecuteChanged();
		try
		{
			await _execute();
		}
		finally
		{
			_running = false;
			RaiseCanExecuteChanged();
		}
	}

	public void RaiseCanExecuteChanged()
	{
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}
public sealed class AsyncCommand<T> : ICommand
{
	private readonly Func<T, Task> _execute;

	private bool _running;

	public event EventHandler? CanExecuteChanged;

	public AsyncCommand(Func<T, Task> execute)
	{
		_execute = execute;
	}

	public bool CanExecute(object? parameter)
	{
		return !_running;
	}

	public async void Execute(object? parameter)
	{
		T t = default;
		int num;
		if (parameter is T)
		{
			t = (T)parameter;
			num = 1;
		}
		else
		{
			num = 0;
		}
		if (num != 0)
		{
			await ExecuteAsync(t);
		}
	}

	public async Task ExecuteAsync(T parameter)
	{
		if (_running)
		{
			return;
		}
		_running = true;
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
		try
		{
			await _execute(parameter);
		}
		finally
		{
			_running = false;
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
		}
	}
}
