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
    /// <summary>Last-resort handler: logs details server-side, returns a safe message (no SQL text, stack traces, secrets).</summary>
    public static void Configure(IApplicationBuilder app) => app.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
        log.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);

        (int status, string code, string msg) = ex switch
        {
            DbUpdateConcurrencyException => (409, "concurrent_edit", "This record was changed by someone else. Refresh and try again."),
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => (409, "duplicate", "A record with the same unique value already exists."),
            SqlException or DbUpdateException or TimeoutException => (503, "database_unavailable", "The database is temporarily unavailable. Please try again shortly."),
            InvalidOperationException { Message: var m } when m.StartsWith("No connection string configured") => (503, "store_not_configured", "The server is not fully configured. Contact your administrator."),
            _ => (500, "server_error", "Something went wrong. Please try again."),
        };
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = msg, Extensions = { ["code"] = code } });
    });
}

/// <summary>Creates the first administrator from configuration (never from source code) when the Users table is empty.</summary>
public static class BootstrapAdmin
{
    public static async Task EnsureAsync(IServiceProvider sp, IConfiguration cfg, ILogger log)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (await db.Users.AnyAsync()) return;

        var emp = cfg["Bootstrap:AdminEmployeeCode"]; var user = cfg["Bootstrap:AdminUsername"]; var pw = cfg["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(emp) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pw))
        { log.LogWarning("No users exist and Bootstrap:Admin* settings are not set; nobody can log in until an administrator is created."); return; }
        if (pw.Length < 12) throw new InvalidOperationException("Bootstrap:AdminPassword must be at least 12 characters.");

        var adminRole = await db.Roles.SingleAsync(r => r.RoleName == RoleNames.Administrator);
        var u = new User { EmployeeCode = emp.Trim(), Username = user.Trim(), DisplayName = cfg["Bootstrap:AdminDisplayName"] ?? "System Administrator", RoleId = adminRole.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
        u.PasswordHash = new IdentityPasswordVerifier().Hash(u, pw);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        log.LogInformation("Bootstrap administrator '{Username}' created. Remove Bootstrap:AdminPassword from configuration now.", u.Username);
    }
}
