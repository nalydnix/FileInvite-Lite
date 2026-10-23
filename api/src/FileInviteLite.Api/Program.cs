using System.Text;
using System.Threading.RateLimiting;
using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Common.Validation;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Data.Seed;
using FileInviteLite.Api.Features.Auth;
using FileInviteLite.Api.Services.Audit;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Host wiring only. Features register themselves through IEndpoint (ADR 0002), so this file is a
// sequential-task hotspot that feature work never touches — see CLAUDE.md rule 2.

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------------------------
builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();   // a missing or short signing key fails the host, not the first request

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// ---------------------------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
                      ?? "Data Source=.local/app.db"));

// One validator per endpoint, found by scan so that a new endpoint never edits this file. They
// run through ValidationFilter<T>, which answers in the shared problem shape.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddScoped<CurrentStaff>();
builder.Services.AddSingleton<JwtTokenIssuer>();

// ---------------------------------------------------------------------------------------------
// Authentication and authorisation
// ---------------------------------------------------------------------------------------------
// Two schemes that never substitute for one another. The portal scheme and policy arrive with the
// Phase 1 portal task; staff endpoints must not be widened to cover clients in the meantime.
builder.Services
    .AddAuthentication(AuthPolicies.Schemes.StaffJwt)
    .AddJwtBearer(AuthPolicies.Schemes.StaffJwt, options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                  ?? new JwtOptions();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwt.SigningKey.Length > 0
                    ? jwt.SigningKey
                    : new string('0', 32))),   // never used: ValidateOnStart fails first
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Staff, policy => policy
        .AddAuthenticationSchemes(AuthPolicies.Schemes.StaffJwt)
        .RequireAuthenticatedUser());

// ---------------------------------------------------------------------------------------------
// Rate limiting (S8.4, S2.10)
// ---------------------------------------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // The limit is configurable so that test classes which are not exercising the limiter can
    // raise it. The production value lives in appsettings.json, not here.
    var loginPermits = builder.Configuration.GetValue("RateLimits:LoginPermitLimit", 5);

    options.AddPolicy(Login.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginPermits,
                Window = TimeSpan.FromMinutes(1),
            }));
});

// ---------------------------------------------------------------------------------------------
// CORS (S10.2)
// ---------------------------------------------------------------------------------------------
const string CorsPolicy = "frontend";
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    var allowed = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";
    policy.WithOrigins(allowed)
          .WithMethods("GET", "POST", "OPTIONS")
          .WithHeaders("Authorization", "Content-Type");
    // No AllowCredentials: the JWT travels as a bearer header and no cookie crosses origins.
}));

builder.Services.AddProblemDetails();
builder.Services.AddUnreadableBodyHandling();

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------------------------
// Converts an unhandled exception into the shared problem shape. Without it, developer exception
// text could reach a client in a misconfigured environment.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseCors(CorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapEndpoints();

// ---------------------------------------------------------------------------------------------
// Startup work
// ---------------------------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    // SQLite will not create a missing folder for the database file, and neither will the file
    // storage. Both default to paths under .local on a fresh clone, so create them here rather
    // than failing the first run with "unable to open database file".
    EnsureDirectoryExists(SqliteDataDirectory(db.Database.GetConnectionString()));
    EnsureDirectoryExists(app.Configuration["Storage:RootPath"]);
    EnsureDirectoryExists(app.Configuration["Email:Sink"]);

    await db.Database.MigrateAsync();
    await DemoDataSeeder.SeedAsync(db, app.Configuration, logger);
}

await app.RunAsync();

static string? SqliteDataDirectory(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return null;
    }

    var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
    return string.IsNullOrWhiteSpace(dataSource) ? null : Path.GetDirectoryName(dataSource);
}

static void EnsureDirectoryExists(string? path)
{
    if (!string.IsNullOrWhiteSpace(path))
    {
        Directory.CreateDirectory(path);
    }
}

/// <summary>Exposed so the integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
