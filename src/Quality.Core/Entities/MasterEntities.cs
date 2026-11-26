namespace Quality.Core.Entities;

// Maps to qa.* tables in the master database (db/01_master_schema.sql).

public class Role
{
    public byte Id { get; set; }
    public string RoleName { get; set; } = "";
}

public class User
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public byte RoleId { get; set; }
    public Role? Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    /// <summary>Bumped on password reset, deactivation and role change. Tokens carry it ("tv" claim); an older value is refused (review Q2).</summary>
    public int TokenVersion { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public byte[]? RowVer { get; set; }
    public List<UserShop> UserShops { get; set; } = new();
}

/// <summary>One row per login name (username and employee code share one namespace, review Q5). Key = UPPER(trimmed value).</summary>
public class LoginIdentifier
{
    public string NormalizedIdentifier { get; set; } = "";
    public int UserId { get; set; }
    /// <summary>'U' username, 'E' employee code, 'B' both (username equals employee code).</summary>
    public string Kind { get; set; } = "U";
}

public class Shop
{
    public int Id { get; set; }
    public string ShopCode { get; set; } = "";
    public string ShopName { get; set; } = "";
    /// <summary>Logical entry-store key ("GDC" / "CNC") resolved server-side to a connection string.</summary>
    public string EntryStoreKey { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class InspectionStage
{
    public int Id { get; set; }
    public string StageCode { get; set; } = "";
    public string StageName { get; set; } = "";
    public byte SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ShopStage
{
    public int ShopId { get; set; }
    public Shop? Shop { get; set; }
    public int InspectionStageId { get; set; }
    public InspectionStage? InspectionStage { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UserShop
{
    public int UserId { get; set; }
    public int ShopId { get; set; }
}

public class Machine
{
    public int Id { get; set; }
    public string MachineCode { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int ShopId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ModifiedAt { get; set; }
    public byte[]? RowVer { get; set; }
}

public class RejectionPhenomenon
{
    public int Id { get; set; }
    public string PhenomenonCode { get; set; } = "";
    /// <summary>Original text, exactly as entered or imported.</summary>
    public string PhenomenonName { get; set; } = "";
    /// <summary>Uniqueness key: see TextNormalizer.NormalizeName.</summary>
    public string NormalizedName { get; set; } = "";
    public int InspectionStageId { get; set; }
    public InspectionStage? InspectionStage { get; set; }
    public bool IsActive { get; set; } = true;
    public int? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public byte[]? RowVer { get; set; }
}

public class AuditLogEntry
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>A plant shift (db/11). Times are plant local time; a shift ends where the next one (by start time) begins.</summary>
public class ShiftDefinition
{
    public int Id { get; set; }
    public string ShiftCode { get; set; } = "";
    public string ShiftName { get; set; } = "";
    public TimeOnly StartTime { get; set; }
    public byte SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class CorrectionStatus
{
    public const string Pending = "Pending", Approved = "Approved", Rejected = "Rejected", Withdrawn = "Withdrawn";
}

/// <summary>A request to correct a saved entry (db/11). Inspector requests; supervisor or administrator decides.</summary>
public class EntryCorrection
{
    public long Id { get; set; }
    public int ShopId { get; set; }
    public long EntryId { get; set; }
    public Guid EntrySubmissionId { get; set; }
    public int RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public string Reason { get; set; } = "";
    public int? ProposedMachineId { get; set; }
    public int? ProposedPhenomenonId { get; set; }
    public int? ProposedQuantity { get; set; }
    public string? ProposedRemarks { get; set; }
    public string Status { get; set; } = CorrectionStatus.Pending;
    public int? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public long? ReplacementEntryId { get; set; }
    public byte[]? RowVer { get; set; }

    /// <summary>No proposal at all: the entry should simply not count.</summary>
    public bool IsReverseOnly => ProposedMachineId is null && ProposedPhenomenonId is null && ProposedQuantity is null && ProposedRemarks is null;
}
