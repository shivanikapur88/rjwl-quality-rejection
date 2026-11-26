namespace Quality.Core.Dtos;

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTime ExpiresAtUtc, int UserId, string EmployeeCode, string DisplayName, string Role);

public sealed record ShopDto(int Id, string ShopCode, string ShopName);
public sealed record StageDto(int Id, string StageCode, string StageName);
public sealed record MachineDto(int Id, string MachineCode, string MachineName);
public sealed record PhenomenonDto(int Id, string PhenomenonCode, string PhenomenonName);

/// <summary>InspectorId and entry time are deliberately NOT part of the request: both come from the server.</summary>
public sealed record CreateEntryRequest(Guid SubmissionId, int ShopId, int StageId, int MachineId, int PhenomenonId, int Quantity, string? Remarks);

public sealed record EntryDto(
    long Id, Guid SubmissionId, int ShopId, string ShopCode, int StageId, string StageCode, string StageName,
    int MachineId, string MachineCode, string MachineName, int PhenomenonId, string PhenomenonName, int Quantity,
    int InspectorId, string InspectorEmployeeCode, string? Remarks, DateTime ServerCreatedAtUtc,
    DateOnly? ProductionDate = null, string? ShiftCode = null, bool IsReversed = false, long? CorrectsEntryId = null,
    // CorrectionStatus: status of the latest correction request for this entry (Pending/Approved/Rejected/Withdrawn) or null.
    string? CorrectionStatus = null);

public sealed record CreateEntryResult(EntryDto Entry, bool AlreadySubmitted);

public sealed record EntryQuery(int? ShopId, int? StageId, DateTime? FromUtc, DateTime? ToUtc, int? Limit);

/// <summary>Paged history request. ToUtc is EXCLUSIVE. Cursor is the NextCursor of the previous page (opaque).</summary>
public sealed record EntryPageQuery(int? ShopId, int? StageId, DateTime? FromUtc, DateTime? ToUtc, int? Limit, string? Cursor);

/// <summary>
/// One page of history, newest first (ServerCreatedAt DESC, ShopId DESC, Id DESC - stable across pages).
/// NextCursor is null on the last page. UnavailableShops lists shop codes whose entry database could not be read:
/// when it is not empty the page is INCOMPLETE and must be presented as such.
/// </summary>
public sealed record EntryPage(IReadOnlyList<EntryDto> Items, string? NextCursor, IReadOnlyList<string> UnavailableShops)
{
    public bool IsComplete => UnavailableShops.Count == 0;
}

public sealed record AdminPhenomenonDto(
    int Id, string PhenomenonCode, string PhenomenonName, int StageId, string StageCode, string StageName,
    IReadOnlyList<string> ShopCodes, bool IsActive, int? CreatedBy, DateTime CreatedAtUtc, int? ModifiedBy, DateTime? ModifiedAtUtc, string? Version = null);

public sealed record CreatePhenomenonRequest(int ShopId, int StageId, string Name, bool IsActive);
/// <summary>Version: optional; the Version from the list/detail the screen was built from. If given and stale, the save is refused with 409 concurrent_edit.</summary>
public sealed record UpdatePhenomenonRequest(string Name, string? Version = null);
public sealed record SetStatusRequest(bool IsActive, string? Version = null);

// ---- Machine management (administrator) ----
public sealed record AdminMachineDto(int Id, string MachineCode, string MachineName, int ShopId, string ShopCode, string ShopName, bool IsActive, string? Version = null);
public sealed record CreateMachineRequest(int ShopId, string MachineCode, string MachineName, bool IsActive);
public sealed record UpdateMachineRequest(string MachineCode, string MachineName, string? Version = null);

// ---- User management (administrator) ----
/// <summary>PasswordHash is never exposed. Administrators implicitly have access to all shops (ShopIds empty, ShopCodes lists all).</summary>
public sealed record AdminUserDto(int Id, string EmployeeCode, string Username, string DisplayName, string Role,
    IReadOnlyList<int> ShopIds, IReadOnlyList<string> ShopCodes, bool IsActive, DateTime CreatedAtUtc, string? Version = null);
public sealed record CreateUserRequest(string EmployeeCode, string Username, string DisplayName, string Role, string Password, IReadOnlyList<int> ShopIds);
public sealed record UpdateUserRequest(string DisplayName, string Role, IReadOnlyList<int> ShopIds, string? Version = null);
public sealed record ResetPasswordRequest(string NewPassword);

// ---- Readiness ----
/// <summary>Warnings do not make the API "unavailable" but need attention (e.g. entries without a production day: run db/12).</summary>
public sealed record ReadinessDto(string Status, IReadOnlyDictionary<string, string> Components, IReadOnlyList<string>? Warnings = null);

// ---- Shifts (#2) ----
public sealed record ShiftDto(string ShiftCode, string ShiftName, string StartTime);
/// <summary>Plant calendar: today's production date and current shift (plant time zone), and the shift list.</summary>
public sealed record CalendarDto(DateOnly ProductionDate, string CurrentShift, string TimeZone, IReadOnlyList<ShiftDto> Shifts);

// ---- Corrections (#1) ----
/// <summary>Reason is required. Leave every Proposed* empty to ask for the entry to be removed; fill any to ask for a corrected entry.</summary>
public sealed record CorrectionRequest(string Reason, int? MachineId = null, int? PhenomenonId = null, int? Quantity = null, string? Remarks = null);
public sealed record CorrectionDecisionRequest(string? Note);
public sealed record CorrectionDto(
    long Id, int ShopId, string ShopCode, long EntryId, EntryDto? Entry,
    int RequestedBy, string RequestedByCode, DateTime RequestedAtUtc, string Reason,
    bool IsReverseOnly, int? ProposedMachineId, string? ProposedMachineCode, int? ProposedPhenomenonId, string? ProposedPhenomenonName,
    int? ProposedQuantity, string? ProposedRemarks,
    string Status, int? DecidedBy, string? DecidedByCode, DateTime? DecidedAtUtc, string? DecisionNote, long? ReplacementEntryId);

// ---- Reports (#4, #6; round 3: drill-down, comparison, rejection rate) ----
/// <summary>
/// MachineId / PhenomenonId narrow the report (dashboard drill-down). Compare adds the previous period of the same length
/// (the days immediately before FromDate) for totals, phenomena and machines.
/// </summary>
public sealed record ReportQuery(DateOnly? FromDate, DateOnly? ToDate, int? ShopId, string? ShiftCode, int? Top,
    int? MachineId = null, int? PhenomenonId = null, bool Compare = false);

/// <summary>
/// Rate fields (round 3, #8). Produced = parts produced according to qa.ProductionCounts for the machines/days/shifts in scope.
/// RejectedWithProduction = rejected parts whose machine+day+shift has a production count. Rate = RejectedWithProduction / Produced
/// (null when nothing was produced per the counts). Coverage = RejectedWithProduction / Quantity: the share of rejected parts the
/// rate is based on. A rate is meaningful only when coverage is high; the dashboard says so.
/// </summary>
public sealed record RateDto(long Produced, int RejectedWithProduction, decimal? Rate, decimal? Coverage);

public sealed record ReportTotal(int Quantity, int Entries, int? PreviousQuantity = null, int? PreviousEntries = null, RateDto? Rate = null);
public sealed record ShopTotalDto(int ShopId, string ShopCode, string ShopName, int Quantity, int Entries, RateDto? Rate = null);
public sealed record DayShiftTotalDto(DateOnly ProductionDate, string ShiftCode, int Quantity, int Entries, long? Produced = null);
public sealed record PhenomenonTotalDto(int PhenomenonId, string PhenomenonName, string ShopCode, int Quantity, int Entries, int? PreviousQuantity = null);
public sealed record MachineTotalDto(int MachineId, string MachineCode, string MachineName, string ShopCode, int Quantity, int Entries,
    int? PreviousQuantity = null, RateDto? Rate = null);
/// <summary>
/// Rejected-part totals for production days FromDate..ToDate inclusive. Reversed entries are excluded.
/// UnavailableShops: shops whose database could not be read. PreviousFromDate/PreviousToDate: the comparison period, when asked for.
/// TopPhenomena is complete up to Top items; PhenomenaCount says how many phenomena exist in the selection (for the Pareto).
/// </summary>
public sealed record ReportDto(DateOnly FromDate, DateOnly ToDate, string? ShiftCode, ReportTotal Total,
    IReadOnlyList<ShopTotalDto> Shops, IReadOnlyList<DayShiftTotalDto> ByDayShift,
    IReadOnlyList<PhenomenonTotalDto> TopPhenomena, IReadOnlyList<MachineTotalDto> TopMachines, IReadOnlyList<string> UnavailableShops,
    DateOnly? PreviousFromDate = null, DateOnly? PreviousToDate = null, int PhenomenaCount = 0, int? MachineId = null, int? PhenomenonId = null)
{
    public bool IsComplete => UnavailableShops.Count == 0;
}

/// <summary>Entries behind a drill-down (newest first, at most Limit). Reversed entries are excluded, as in the totals.</summary>
public sealed record ReportEntriesDto(IReadOnlyList<EntryDto> Items, bool HasMore, IReadOnlyList<string> UnavailableShops);

// ---- Production counts (round 3, #8) ----
public sealed record ProductionCountRow(string ShopCode, string MachineCode, DateOnly ProductionDate, string ShiftCode, int ProducedQuantity);
/// <summary>DryRun: validate and report only. Rows are upserted by (machine, production day, shift); the whole upload is rejected if any row is invalid.</summary>
public sealed record ProductionCountUpload(IReadOnlyList<ProductionCountRow> Rows, bool DryRun = true, string? Source = null);
public sealed record ProductionCountProblem(int Row, string Problem);
public sealed record ProductionCountResult(bool Applied, int Rows, int Inserted, int Updated, int Unchanged, IReadOnlyList<ProductionCountProblem> Problems);

// ---- Machine QR (#8) ----
/// <summary>Result of scanning a machine label: where the entry form should open.</summary>
public sealed record MachineLookupDto(ShopDto Shop, IReadOnlyList<StageDto> Stages, MachineDto Machine);
