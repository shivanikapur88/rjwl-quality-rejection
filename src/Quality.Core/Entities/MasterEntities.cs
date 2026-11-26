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
