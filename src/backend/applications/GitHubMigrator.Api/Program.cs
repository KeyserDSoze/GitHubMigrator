using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Blobs;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using GitHubMigrator.Infrastructure.AzureDevOps;
using GitHubMigrator.Infrastructure.GitHub;
using GitHubMigrator.Infrastructure.KeyVault;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.AzureStorage;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required.");
var signingKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is required.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 UTF-8 bytes.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.User.RequireUniqueEmail = false;
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = !builder.Environment.IsDevelopment();
    options.Password.RequireUppercase = !builder.Environment.IsDevelopment();
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = !builder.Environment.IsDevelopment();
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();

builder.Services.AddScoped<JwtIssuer>();
builder.Services.AddScoped<MigrationPlanner>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IConnectionRepository, PostgresConnectionRepository>();
builder.Services.AddScoped<ConnectionService>();
builder.Services.AddScoped<IDiscoveryRepository, PostgresDiscoveryRepository>();
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddScoped<IIdentityMappingDraftRepository, PostgresIdentityMappingDraftRepository>();
builder.Services.AddScoped<IdentityMappingService>();
builder.Services.AddHttpClient<AzureDevOpsDiscoveryReader>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IDiscoveryReader>(s => s.GetRequiredService<AzureDevOpsDiscoveryReader>());
builder.Services.AddHttpClient<AzureDevOpsConnectionValidator>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<GitHubConnectionValidator>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IConnectionValidator>(s => s.GetRequiredService<AzureDevOpsConnectionValidator>());
builder.Services.AddTransient<IConnectionValidator>(s => s.GetRequiredService<GitHubConnectionValidator>());

var credentialProvider = builder.Configuration["Secrets:Provider"] ??
    (builder.Environment.IsDevelopment() ? "EncryptedPostgreSql" : "KeyVault");
if (credentialProvider.Equals("KeyVault", StringComparison.OrdinalIgnoreCase))
{
    var vaultUri = builder.Configuration["Secrets:VaultUri"] ??
        throw new InvalidOperationException("Secrets:VaultUri is required for KeyVault.");
    builder.Services.AddSingleton(new SecretClient(new Uri(vaultUri), new DefaultAzureCredential()));
    builder.Services.AddScoped<IConnectionSecretStore, KeyVaultConnectionSecretStore>();
}
else if (credentialProvider.Equals("EncryptedPostgreSql", StringComparison.OrdinalIgnoreCase) &&
    builder.Environment.IsDevelopment())
{
    var encryptionKey = builder.Configuration["Secrets:LocalEncryptionKey"] ??
        throw new InvalidOperationException("Secrets:LocalEncryptionKey is required for local development.");
    builder.Services.AddScoped<IConnectionSecretStore>(s =>
        new PostgresEncryptedConnectionSecretStore(s.GetRequiredService<AppDbContext>(), encryptionKey));
}
else throw new InvalidOperationException("Local encrypted credential storage is restricted to Development.");
var storageConnection = builder.Configuration["Storage:ConnectionString"];
if (!string.IsNullOrWhiteSpace(storageConnection))
{
    builder.Services.AddSingleton<IArtifactStorage>(_ => new BlobArtifactStorage(
        new BlobContainerClient(storageConnection, builder.Configuration["Storage:Container"] ?? "migration-artifacts")));
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "GitHubMigrator",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "GitHubMigrator.Web",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("PasswordChanged", policy =>
        policy.RequireAuthenticatedUser().RequireClaim("password_changed", "true")));

var frontendOrigin = builder.Configuration["Frontend:Origin"] ?? "http://localhost:5173";
builder.Services.AddCors(options =>
    options.AddPolicy("Frontend", policy => policy.WithOrigins(frontendOrigin).AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("external", limiter =>
    {
        limiter.PermitLimit = 12;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("GitHubMigrator.Api"))
    .WithTracing(trace => trace.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
if (builder.Configuration["Observability:Provider"]?.Equals("AzureMonitor", StringComparison.OrdinalIgnoreCase) == true)
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        throw new InvalidOperationException("APPLICATIONINSIGHTS_CONNECTION_STRING is required for AzureMonitor.");
    telemetry.UseAzureMonitor(options =>
        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
}
else
{
    telemetry.UseOtlpExporter();
}

var bootstrapPassword = builder.Configuration["Bootstrap:Password"];
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(bootstrapPassword) || bootstrapPassword == "superadmin"))
    throw new InvalidOperationException("A strong non-default Bootstrap:Password is required outside Development.");

var app = builder.Build();

app.UseCors("Frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

await Bootstrap.InitializeAsync(app.Services, builder.Configuration, builder.Environment, app.Logger);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "GitHubMigrator.Api" }));
app.MapGet("/api/ready", async Task<IResult> (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var auth = app.MapGroup("/api/auth");
auth.MapPost("/login", async Task<IResult> (LoginRequest request, UserManager<ApplicationUser> users,
    JwtIssuer jwt, ILogger<Program> logger) =>
{
    var user = await users.FindByNameAsync(request.Username ?? "");
    if (user is null || await users.IsLockedOutAsync(user))
        return Results.Unauthorized();
    if (!await users.CheckPasswordAsync(user, request.Password ?? ""))
    {
        await users.AccessFailedAsync(user);
        logger.LogWarning("Authentication rejected: invalid credentials");
        return Results.Unauthorized();
    }
    await users.ResetAccessFailedCountAsync(user);
    logger.LogInformation("Platform user authenticated UserId={UserId} MustChangePassword={MustChangePassword}",
        user.Id, user.MustChangePassword);
    return Results.Ok(await jwt.CreateAsync(user));
}).RequireRateLimiting("login");

auth.MapPost("/change-password", async Task<IResult> (ChangePasswordRequest request, ClaimsPrincipal principal,
    UserManager<ApplicationUser> users, JwtIssuer jwt, ILogger<Program> logger) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var result = await users.ChangePasswordAsync(user, request.CurrentPassword ?? "", request.NewPassword ?? "");
    if (!result.Succeeded)
        return Results.ValidationProblem(result.Errors.ToDictionary(x => x.Code, x => new[] { x.Description }));
    user.MustChangePassword = false;
    await users.UpdateAsync(user);
    logger.LogInformation("Password rotated for platform user UserId={UserId}", user.Id);
    return Results.Ok(await jwt.CreateAsync(user));
}).RequireAuthorization();

app.MapPost("/api/users", async Task<IResult> (CreateUserRequest request, ClaimsPrincipal principal,
    UserManager<ApplicationUser> users, RoleManager<IdentityRole<Guid>> roles, ILogger<Program> logger) =>
{
    var actor = await users.GetUserAsync(principal);
    if (actor is null || actor.MustChangePassword) return Results.Forbid();
    var actorRoles = await users.GetRolesAsync(actor);
    var targetRole = request.Role?.ToLowerInvariant();
    if (targetRole is not ("superadmin" or "admin" or "user"))
        return Results.BadRequest(new { error = "Invalid role" });
    if (!actorRoles.Contains("superadmin") && targetRole != "user")
        return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.TemporaryPassword))
        return Results.BadRequest(new { error = "Username and temporary password are required." });
    var user = new ApplicationUser { UserName = request.Username.Trim(), MustChangePassword = true };
    var created = await users.CreateAsync(user, request.TemporaryPassword);
    if (!created.Succeeded)
        return Results.ValidationProblem(created.Errors.ToDictionary(x => x.Code, x => new[] { x.Description }));
    await users.AddToRoleAsync(user, targetRole);
    logger.LogInformation("Platform user created UserId={UserId} Role={Role} ActorId={ActorId}", user.Id, targetRole, actor.Id);
    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.UserName, role = targetRole, user.MustChangePassword });
}).RequireAuthorization("PasswordChanged").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    { Roles = "superadmin,admin" });

app.MapGet("/api/providers", (ConnectionService service) => Results.Ok(new
{
    available = service.SupportedProviders(),
    planned = new[] { "azure-devops-server", "tfs-server", "github-enterprise-server" }
})).RequireAuthorization("PasswordChanged");

app.MapConnectionEndpoints();
app.MapDiscoveryEndpoints();
app.MapIdentityMappingEndpoints();

app.Run();

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record CreateUserRequest(string? Username, string? TemporaryPassword, string? Role);
public partial class Program { }
