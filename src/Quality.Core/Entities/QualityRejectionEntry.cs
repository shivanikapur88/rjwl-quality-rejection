namespace Quality.Core.Entities;

/// <summary>
/// Maps to qa.QualityRejectionEntries in GDC_AndonDB or MachineShopDB.
/// Snapshots make history immune to later master edits or deactivation.
/// </summary>
public class QualityRejectionEntry
{
    public long Id { get; set; }
    public Guid SubmissionId { get; set; }
    public int ShopId { get; set; }
    public string ShopCode { get; set; } = "";
    public int InspectionStageId { get; set; }
    public string StageCode { get; set; } = "";
    public string StageName { get; set; } = "";
    public int MachineId { get; set; }
    public string MachineCode { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int RejectionPhenomenonId { get; set; }
    public string PhenomenonNameSnapshot { get; set; } = "";
    /// <summary>Number of rejected parts (1..9999).</summary>
    public int Quantity { get; set; } = 1;
    public int InspectorId { get; set; }
    public string InspectorEmployeeCode { get; set; } = "";
    public string? Remarks { get; set; }
    public DateTime ServerCreatedAt { get; set; }
}
