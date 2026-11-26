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
    int InspectorId, string InspectorEmployeeCode, string? Remarks, DateTime ServerCreatedAtUtc);

public sealed record CreateEntryResult(EntryDto Entry, bool AlreadySubmitted);

public sealed record EntryQuery(int? ShopId, int? StageId, DateTime? FromUtc, DateTime? ToUtc, int? Limit);

public sealed record AdminPhenomenonDto(
    int Id, string PhenomenonCode, string PhenomenonName, int StageId, string StageCode, string StageName,
    IReadOnlyList<string> ShopCodes, bool IsActive, int? CreatedBy, DateTime CreatedAtUtc, int? ModifiedBy, DateTime? ModifiedAtUtc);

public sealed record CreatePhenomenonRequest(int ShopId, int StageId, string Name, bool IsActive);
public sealed record UpdatePhenomenonRequest(string Name);
public sealed record SetStatusRequest(bool IsActive);

// ---- Machine management (administrator) ----
public sealed record AdminMachineDto(int Id, string MachineCode, string MachineName, int ShopId, string ShopCode, string ShopName, bool IsActive);
public sealed record CreateMachineRequest(int ShopId, string MachineCode, string MachineName, bool IsActive);
public sealed record UpdateMachineRequest(string MachineCode, string MachineName);

// ---- User management (administrator) ----
/// <summary>PasswordHash is never exposed. Administrators implicitly have access to all shops (ShopIds empty, ShopCodes lists all).</summary>
public sealed record AdminUserDto(int Id, string EmployeeCode, string Username, string DisplayName, string Role,
    IReadOnlyList<int> ShopIds, IReadOnlyList<string> ShopCodes, bool IsActive, DateTime CreatedAtUtc);
public sealed record CreateUserRequest(string EmployeeCode, string Username, string DisplayName, string Role, string Password, IReadOnlyList<int> ShopIds);
public sealed record UpdateUserRequest(string DisplayName, string Role, IReadOnlyList<int> ShopIds);
public sealed record ResetPasswordRequest(string NewPassword);
