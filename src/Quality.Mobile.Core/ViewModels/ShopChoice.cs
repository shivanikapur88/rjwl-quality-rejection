// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using Quality.Core.Dtos;
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Core.ViewModels;

public sealed class ShopChoice : ObservableObject
{
	private bool _selected;

	public ShopDto Shop { get; }

	public string Name => Shop.ShopName;

	public bool IsSelected
	{
		get
		{
			return _selected;
		}
		set
		{
			Set(ref _selected, value, "IsSelected");
		}
	}

	public ShopChoice(ShopDto shop, bool selected)
	{
		Shop = shop;
		_selected = selected;
	}
}
