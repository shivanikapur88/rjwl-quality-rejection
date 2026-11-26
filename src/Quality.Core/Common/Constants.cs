namespace Quality.Core.Common;

public static class RoleNames
{
    public static readonly string[] All = { Administrator, QualityInspector, Supervisor };
    public const string Administrator = "Administrator";
    public const string QualityInspector = "QualityInspector";
    public const string Supervisor = "Supervisor";
}

public static class StageCodes
{
    public const string GdcShop = "GDC_SHOP";
    public const string CncShop = "CNC_SHOP";
    public const string PreTreatment = "PRE_TREATMENT";
    public const string Final = "FINAL";
}

public static class ShopCodes
{
    public const string Gdc = "GDC";   // entries -> GDC_AndonDB
    public const string Cnc = "CNC";   // entries -> MachineShopDB
    public const string Pre = "PRE";   // Pre-treatment Inspection shop; entries -> database chosen in configuration
    public const string Final = "FINAL"; // FINAL Inspection shop; entries -> database chosen in configuration
}

public static class AuditActions
{
    public const string PhenomenonCreated = "PHENOMENON_CREATED";
    public const string PhenomenonEdited = "PHENOMENON_EDITED";
    public const string PhenomenonActivated = "PHENOMENON_ACTIVATED";
    public const string PhenomenonDeactivated = "PHENOMENON_DEACTIVATED";
    public const string PhenomenonDeleted = "PHENOMENON_DELETED";
    public const string ExcelImport = "EXCEL_IMPORT";
    public const string MachineCreated = "MACHINE_CREATED";
    public const string MachineEdited = "MACHINE_EDITED";
    public const string MachineActivated = "MACHINE_ACTIVATED";
    public const string MachineDeactivated = "MACHINE_DEACTIVATED";
    public const string MachineDeleted = "MACHINE_DELETED";
    public const string UserCreated = "USER_CREATED";
    public const string UserEdited = "USER_EDITED";
    public const string UserActivated = "USER_ACTIVATED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string UserPasswordReset = "USER_PASSWORD_RESET";
}
