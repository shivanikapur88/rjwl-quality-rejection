using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Quality.Core.Common;
using Quality.Core.Services;
using Quality.Data;
using Quality.Core.Entities;
using Quality.Core.Abstractions;

namespace Quality.Api.Infrastructure;

public abstract class ApiControllerBase : ControllerBase
{
    protected CurrentUser Me => User.ToCurrentUser();

    protected static ObjectResult ProblemFor(int status, string code, string message) =>
        new(new ProblemDetails { Status = status, Title = message, Extensions = { ["code"] = code } }) { StatusCode = status };

    protected IActionResult Map<T>(ServiceResult<T> r, Func<T, IActionResult>? onOk = null)
    {
        if (r.IsSuccess) return onOk is null ? Ok(r.Value) : onOk(r.Value!);
        var e = r.Error!;
        var status = e.Kind switch
        {
            ErrorKind.Validation => 400, ErrorKind.Unauthorized => 401, ErrorKind.Forbidden => 403,
            ErrorKind.NotFound => 404, ErrorKind.Conflict => 409, _ => 500,
        };
        return ProblemFor(status, e.Code, e.Message);
    }
}

public static class ErrorHandling
{
    /// <summary>
    /// Last-resort handler: logs details server-side, returns a safe message (no SQL text, stack traces, secrets).
    /// Only real connectivity/timeouts are reported as 503 "database unavailable"; constraint, truncation and deadlock
    /// errors get their own codes (previously they were all reported as 503).
    /// </summary>
    public static void Configure(IApplicationBuilder app) => app.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
        log.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);

        var (status, code, msg) = Classify(ex);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = msg, Extensions = { ["code"] = code } });
    });

    public static (int Status, string Code, string Message) Classify(Exception? ex)
    {
        var sql = ex as SqlException ?? ex?.InnerException as SqlException ?? ex?.InnerException?.InnerException as SqlException;
        return ex switch
        {
            ConcurrencyConflictException or DbUpdateConcurrencyException => (409, "concurrent_edit", "This record was changed by someone else. Refresh and try again."),
            UniqueViolationException => (409, "duplicate", "A record with the same unique value already exists."),
            BadHttpRequestException => (400, "bad_request", "The request was not valid."),
            OperationCanceledException when ex is not TimeoutException => (499, "cancelled", "The request was cancelled."),
            InvalidOperationException { Message: var m } when m.StartsWith("No connection string configured", StringComparison.Ordinal)
                => (503, "store_not_configured", "The server is not fully configured. Contact your administrator."),
            _ when sql is not null => ClassifySql(sql),
            TimeoutException => (503, "database_unavailable", "The database is temporarily unavailable. Please try again shortly."),
            _ => (500, "server_error", "Something went wrong. Please try again."),
        };
    }

    private static (int, string, string) ClassifySql(SqlException sql) => sql.Number switch
    {
        2601 or 2627 => (409, "duplicate", "A record with the same unique value already exists."),
        547 => (409, "constraint_violation", "This change conflicts with related data. Refresh and try again."),
        2628 or 8152 => (400, "value_too_long", "One of the values is too long."),
        1205 => (409, "concurrent_change", "Someone else was changing the same data at the same moment. Please try again."),
        50090 => (409, "last_admin", "At least one active administrator must remain."),
        // Connectivity, login, timeouts and server-unavailable errors.
        -2 or -1 or 2 or 53 or 40 or 64 or 121 or 233 or 4060 or 4064 or 10053 or 10054 or 10060 or 10061 or 11001 or 18456 or 40613 or 1222
            => (503, "database_unavailable", "The database is temporarily unavailable. Please try again shortly."),
        _ when sql.Class >= 20 => (503, "database_unavailable", "The database is temporarily unavailable. Please try again shortly."),
        _ => (500, "server_error", "Something went wrong. Please try again."),
    };
}

/// <summary>
/// Creates the first administrator from configuration (never from source code) when the Users table is empty.
/// Runs in the background with retries, so the API still starts (and /api/health/ready reports the problem) when SQL Server is
/// down at start-up; previously that crashed the process.
/// </summary>
public sealed class BootstrapAdmin : BackgroundService
{
    private readonly IServiceProvider _sp; private readonly IConfiguration _cfg; private readonly ILogger<BootstrapAdmin> _log;
    public BootstrapAdmin(IServiceProvider sp, IConfiguration cfg, ILogger<BootstrapAdmin> log) => (_sp, _cfg, _log) = (sp, cfg, log);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        for (var attempt = 1; !stop.IsCancellationRequested; attempt++)
        {
            try { await EnsureAsync(_sp, _cfg, _log, stop); return; }
            catch (Exception ex) when (ex is SqlException or DbUpdateException or TimeoutException or InvalidOperationException { InnerException: SqlException })
            {
                var wait = TimeSpan.FromSeconds(Math.Min(300, 5 * attempt));
                _log.LogWarning(ex, "Bootstrap administrator check failed (attempt {Attempt}); retrying in {Wait}.", attempt, wait);
                try { await Task.Delay(wait, stop); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public static async Task EnsureAsync(IServiceProvider sp, IConfiguration cfg, ILogger log, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (await db.Users.AnyAsync(ct)) return;

        var emp = cfg["Bootstrap:AdminEmployeeCode"]?.Trim(); var user = cfg["Bootstrap:AdminUsername"]?.Trim(); var pw = cfg["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(emp) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pw))
        { log.LogWarning("No users exist and Bootstrap:Admin* settings are not set; nobody can log in until an administrator is created."); return; }
        // Same rules as in-app user creation (review Q6: employee code max 30 = column size).
        if (LoginRules.EmployeeCodeProblem(emp) is { } e1) { log.LogError("Bootstrap:AdminEmployeeCode is invalid. {Problem}", e1); return; }
        if (LoginRules.UsernameProblem(user) is { } e2) { log.LogError("Bootstrap:AdminUsername is invalid. {Problem}", e2); return; }
        if (pw.Length < 12) { log.LogError("Bootstrap:AdminPassword must be at least 12 characters; bootstrap administrator not created."); return; }

        var adminRole = await db.Roles.SingleAsync(r => r.RoleName == RoleNames.Administrator, ct);
        var u = new User { EmployeeCode = emp, Username = user, DisplayName = cfg["Bootstrap:AdminDisplayName"] ?? "System Administrator", RoleId = adminRole.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
        u.PasswordHash = new IdentityPasswordVerifier().Hash(u, pw);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Users.Add(u);
        await db.SaveChangesAsync(ct);
        db.LoginIdentifiers.AddRange(LoginRules.IdentifiersFor(u));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("Bootstrap administrator '{Username}' created. Remove Bootstrap:AdminPassword from configuration now.", u.Username);
    }
}
