using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Quality.Api.Infrastructure;
using Quality.Core.Abstractions;
using Quality.Core.Services;
using Quality.Data;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---- Required configuration: fail fast with a clear message, never fall back to a hard-coded secret ----
string Require(string key) => !string.IsNullOrWhiteSpace(cfg[key]) ? cfg[key]! : throw new InvalidOperationException($"Missing configuration value '{key}'. See README (user-secrets / environment variables).");
var masterCs = Require("ConnectionStrings:Master");
// One entry store per shop. PRE and FINAL may point at the same database (entries are separated by ShopId).
var entryStores = new Dictionary<string, string>
{
    ["GDC"] = Require("ConnectionStrings:GdcEntries"), ["CNC"] = Require("ConnectionStrings:CncEntries"),
    ["PRE"] = Require("ConnectionStrings:PreEntries"), ["FINAL"] = Require("ConnectionStrings:FinalEntries"),
};
var jwt = cfg.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
if (Encoding.UTF8.GetByteCount(Require("Jwt:Key")) < 32) throw new InvalidOperationException("Jwt:Key must be at least 32 bytes.");
jwt.Key = cfg["Jwt:Key"]!;

builder.Services.Configure<JwtSettings>(cfg.GetSection("Jwt"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<MasterDbContext>(o => o.UseSqlServer(masterCs));
builder.Services.AddScoped<IMasterData, EfMasterData>();
builder.Services.AddScoped<IEntryStoreRouter>(_ => new EntryStoreRouter(entryStores));
builder.Services.AddScoped<ShopAccessGuard>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<RejectionEntryService>();
builder.Services.AddScoped<PhenomenaAdminService>();
builder.Services.AddScoped<MachineAdminService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<EntryCorrectionService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<MachineLookupService>();
builder.Services.AddScoped<ProductionCountService>();   // round 3
builder.Services.AddSingleton(new CorrectionOptions(cfg.GetValue("Corrections:MaxAgeDays", 30)));
builder.Services.AddSingleton<IShiftCalendarProvider, ShiftCalendarProvider>();
builder.Services.AddSingleton<IdentityPasswordVerifier>();
builder.Services.AddSingleton<IPasswordVerifier>(sp => sp.GetRequiredService<IdentityPasswordVerifier>());
builder.Services.AddSingleton<IPasswordHashService>(sp => sp.GetRequiredService<IdentityPasswordVerifier>());
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddHostedService<BootstrapAdmin>();   // retries in the background; the API starts even if SQL Server is down

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "sub", RoleClaimType = "role",
    };
    o.Events = new JwtBearerEvents
    {
        // Deactivation, role change and password reset take effect immediately, not at token expiry (review Q2).
        OnTokenValidated = TokenRevocation.ValidateAsync,
        // Lets the app tell "session expired" from "not allowed".
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            var expired = ctx.AuthenticateFailure is SecurityTokenExpiredException;
            ctx.Response.StatusCode = 401; ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 401, Title = expired ? "Your session has expired. Please sign in again." : "Authentication required.", Extensions = { ["code"] = expired ? "token_expired" : "unauthenticated" } });
        },
    };
});
builder.Services.AddAuthorization();
// Review Q4: the old single server-wide "login" window (10/minute for everybody) is gone. Failed attempts are counted per
// client + login, per login and per client by LoginThrottle; this limiter only caps raw request volume PER CLIENT ADDRESS.
// Generous by default: many tablets may sign in through one plant address at shift change.
var loginRequestsPerMinute = cfg.GetValue("LoginThrottle:RequestsPerMinutePerClient", 300);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
// Behind a reverse proxy the client address must come from X-Forwarded-For, or every device would share one throttle bucket.
var knownProxies = cfg.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var ip in knownProxies) o.KnownProxies.Add(IPAddress.Parse(ip));
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Quality Rejection API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header, Description = "Paste the token from POST /api/auth/login" });
    // Swashbuckle 10 / Microsoft.OpenApi 2: schemes are referenced with OpenApiSecuritySchemeReference.
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = new List<string>() });
});

var app = builder.Build();
if (knownProxies.Length > 0) app.UseForwardedHeaders();
app.UseExceptionHandler(ErrorHandling.Configure);
app.UseHttpsRedirection();
// Feature #6: read-only web dashboard (static files under wwwroot/dashboard) with a strict content-security policy:
// only this site's own scripts/styles, no inline script, no framing. The pages call the same API with the same roles.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/dashboard"))
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.CacheControl = "no-cache";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment() || cfg.GetValue<bool>("Swagger:Enabled")) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }   // for WebApplicationFactory-based integration tests
