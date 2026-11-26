// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System.Collections.Generic;
using System.Threading.Tasks;
using Quality.Core.Dtos;

namespace Quality.Mobile.Core.Services;

public interface IApiClient
{
	Task<ApiResult<Unit>> HealthAsync();

	Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request);

	Task<ApiResult<List<ShopDto>>> GetShopsAsync();

	Task<ApiResult<List<StageDto>>> GetStagesAsync(int shopId);

	Task<ApiResult<List<MachineDto>>> GetMachinesAsync(int shopId, int stageId);

	Task<ApiResult<List<PhenomenonDto>>> GetPhenomenaAsync(int shopId, int stageId);

	Task<ApiResult<CreateEntryResult>> SubmitEntryAsync(CreateEntryRequest request);

	Task<ApiResult<List<EntryDto>>> GetEntriesAsync(int limit = 50);

	/// <summary>One page of history (GET /api/rejection-entries/page). Pass the previous page's NextCursor to continue.</summary>
	Task<ApiResult<EntryPage>> GetEntriesPageAsync(string? cursor, int limit = 50);

	Task<ApiResult<List<AdminPhenomenonDto>>> AdminListAsync(int? shopId, int? stageId, bool? isActive, string? search);

	Task<ApiResult<AdminPhenomenonDto>> AdminCreateAsync(CreatePhenomenonRequest request);

	Task<ApiResult<AdminPhenomenonDto>> AdminUpdateAsync(int id, UpdatePhenomenonRequest request);

	Task<ApiResult<AdminPhenomenonDto>> AdminSetStatusAsync(int id, bool isActive, string? version = null);

	Task<ApiResult<Unit>> AdminDeleteAsync(int id);

	Task<ApiResult<List<AdminMachineDto>>> AdminMachinesListAsync(int? shopId, bool? isActive, string? search);

	Task<ApiResult<AdminMachineDto>> AdminMachineCreateAsync(CreateMachineRequest request);

	Task<ApiResult<AdminMachineDto>> AdminMachineUpdateAsync(int id, UpdateMachineRequest request);

	Task<ApiResult<AdminMachineDto>> AdminMachineSetStatusAsync(int id, bool isActive, string? version = null);

	Task<ApiResult<Unit>> AdminMachineDeleteAsync(int id);

	Task<ApiResult<List<AdminUserDto>>> AdminUsersListAsync(string? search, bool? isActive);

	Task<ApiResult<AdminUserDto>> AdminUserCreateAsync(CreateUserRequest request);

	Task<ApiResult<AdminUserDto>> AdminUserUpdateAsync(int id, UpdateUserRequest request);

	Task<ApiResult<AdminUserDto>> AdminUserSetStatusAsync(int id, bool isActive, string? version = null);

	Task<ApiResult<Unit>> AdminUserResetPasswordAsync(int id, ResetPasswordRequest request);
}
