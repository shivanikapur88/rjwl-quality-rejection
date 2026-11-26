// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;
using Quality.Core.Dtos;

namespace Quality.Mobile.Core.ViewModels;

public sealed class AdminItem
{
	public AdminPhenomenonDto Dto { get; }

	public string Name => Dto.PhenomenonName;

	public string Code => Dto.PhenomenonCode;

	public bool IsActive => Dto.IsActive;

	public string StatusText => Dto.IsActive ? "Active" : "Inactive";

	public string StageLine => Dto.StageName + " · " + string.Join(", ", Dto.ShopCodes);

	public string ToggleText => Dto.IsActive ? "Deactivate" : "Activate";

	public string AuditText
	{
		get
		{
			string text = $"Created {Dto.CreatedAtUtc.ToLocalTime():dd MMM yyyy HH:mm}";
			int? createdBy = Dto.CreatedBy;
			string text2;
			if (createdBy.HasValue)
			{
				int valueOrDefault = createdBy.GetValueOrDefault();
				text2 = $" by user #{valueOrDefault}";
			}
			else
			{
				text2 = " (Excel import)";
			}
			string text3 = text + text2;
			DateTime? modifiedAtUtc = Dto.ModifiedAtUtc;
			if (modifiedAtUtc.HasValue)
			{
				DateTime valueOrDefault2 = modifiedAtUtc.GetValueOrDefault();
				if (true)
				{
					string text4 = text3;
					string text5 = $" · Modified {valueOrDefault2.ToLocalTime():dd MMM yyyy HH:mm}";
					createdBy = Dto.ModifiedBy;
					string text6;
					if (createdBy.HasValue)
					{
						int valueOrDefault3 = createdBy.GetValueOrDefault();
						text6 = $" by user #{valueOrDefault3}";
					}
					else
					{
						text6 = "";
					}
					text3 = text4 + text5 + text6;
				}
			}
			return text3;
		}
	}

	public AdminItem(AdminPhenomenonDto d)
	{
		Dto = d;
	}
}
