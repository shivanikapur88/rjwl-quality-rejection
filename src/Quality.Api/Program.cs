using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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
builder.Services.AddSingleton<IdentityPasswordVerifier>();
builder.Services.AddSingleton<IPasswordVerifier>(sp => sp.GetRequiredService<IdentityPasswordVerifier>());
builder.Services.AddSingleton<IPasswordHashService>(sp => sp.GetRequiredService<IdentityPasswordVerifier>());
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();

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
        // A deactivated user or changed role takes effect immediately, not at token expiry.
        OnTokenValidated = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<MasterDbContext>();
            int id = int.Parse(ctx.Principal!.FindFirst("sub")!.Value);
            var u = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.IsActive, Role = x.Role!.RoleName }).FirstOrDefaultAsync();
            if (u is null || !u.IsActive || u.Role != ctx.Principal.FindFirst("role")?.Value) ctx.Fail("User no longer valid.");
        },
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
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddFixedWindowLimiter("login", w => { w.PermitLimit = 10; w.Window = TimeSpan.FromMinutes(1); w.QueueLimit = 0; });
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Quality Rejection API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header, Description = "Paste the token from POST /api/auth/login" });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() });
});

var app = builder.Build();
app.UseExceptionHandler(ErrorHandling.Configure);
app.UseHttpsRedirection();
if (app.Environment.IsDevelopment() || cfg.GetValue<bool>("Swagger:Enabled")) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await BootstrapAdmin.EnsureAsync(app.Services, cfg, app.Logger);
app.Run();

public partial class Program { }   // for WebApplicationFactory-based integration tests
