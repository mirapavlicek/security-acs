using System.Threading.RateLimiting;
using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Čeština v HTML výstupu bez číselných entit.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(
        System.Text.Unicode.UnicodeRanges.All));

// ---------- Databáze ----------
// Produkce: MariaDB Galera (ConnectionStrings:Default z /etc/acs/acs.env).
// Vývoj/testy: SQLite (bez konfigurace se použije lokální soubor acs.db).
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = null;

var dbProvider = builder.Configuration["Database:Provider"];
if (string.IsNullOrWhiteSpace(dbProvider))
    dbProvider = connectionString is null ? "Sqlite" : "MariaDb";

builder.Services.AddDbContext<AcsDbContext>(options =>
{
    if (dbProvider.Equals("MariaDb", StringComparison.OrdinalIgnoreCase))
    {
        options.UseMySql(
            connectionString!,
            ServerVersion.Create(new Version(10, 6, 0),
                Pomelo.EntityFrameworkCore.MySql.Infrastructure.ServerType.MariaDb),
            mysql => mysql.EnableRetryOnFailure(maxRetryCount: 5));
    }
    else
    {
        options.UseSqlite(connectionString ?? "Data Source=acs.db");
    }
});

// Data Protection klíče v DB → oba HA nody sdílí šifrování cookies (bezestavovost).
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AcsDbContext>()
    .SetApplicationName("AcsWeb");

// ---------- Služby ----------
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<LdapAuthenticator>();
builder.Services.AddScoped<UserAuthenticationService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddHttpClient<WinPakClient>();

// Synchronizace číselníků (ruční tlačítka + automatický plánovač s DB zámkem).
builder.Services.AddScoped<Acs.Infrastructure.Sync.ReaderSyncService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.EmployeeSyncService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.AccessSyncService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.EmployeeSourceFactory>();
builder.Services.AddHttpClient(nameof(Acs.Infrastructure.Sync.ApiEmployeeSource));
builder.Services.AddHostedService<Acs.Infrastructure.Sync.SyncScheduler>();

// Schvalovací workflow, fronta správce karet a e-mailové notifikace.
builder.Services.AddScoped<Acs.Infrastructure.Notifications.INotificationService,
    Acs.Infrastructure.Notifications.EmailNotificationService>();
builder.Services.AddScoped<Acs.Infrastructure.Workflow.RequestWorkflowService>();
builder.Services.AddScoped<Acs.Infrastructure.Workflow.CardAdminService>();

// ---------- Autentizace a autorizace ----------
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "acs-auth";
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // V produkci (za TLS-terminující HAProxy) vždy Secure; ve vývoji přes
        // plné HTTP by Always cookie zahodilo, proto SameAsRequest.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

// Aplikace běží za HAProxy, který terminuje TLS — čti X-Forwarded-* hlavičky,
// aby aplikace znala reálné schéma (https) a IP klienta (rate-limit, audit).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // HAProxy je v interní síti; známé proxy se nevynucují (jinak nutná konfigurace IP).
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Rate-limit na přihlašování (obrana proti hádání hesel), per IP klienta.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Admin", p => p.RequireRole(nameof(AppRole.Admin)));
    options.AddPolicy("CatalogManager",
        p => p.RequireRole(nameof(AppRole.Admin), nameof(AppRole.CatalogManager)));
    options.AddPolicy("CardAdmin",
        p => p.RequireRole(nameof(AppRole.Admin), nameof(AppRole.CardAdmin)));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Denied");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
    options.Conventions.AuthorizeFolder("/Catalog", "CatalogManager");
});

var app = builder.Build();

// ---------- Inicializace DB (migrace + seed lokálního admina) ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AcsDbContext>();
    var initLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Acs.DatabaseInitializer");
    await DatabaseInitializer.InitializeAsync(db, initLogger);
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Bezpečnostní hlavičky pro všechny odpovědi.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "same-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; "
        + "script-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();

// Vynucení změny výchozího hesla lokálních účtů.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && context.User.HasClaim("must_change_password", "1")
        && !context.Request.Path.StartsWithSegments("/Account")
        && !context.Request.Path.StartsWithSegments("/css")
        && !context.Request.Path.StartsWithSegments("/lib"))
    {
        context.Response.Redirect("/Account/ChangePassword");
        return;
    }

    await next(context);
});

app.UseAuthorization();

// Healthcheck pro HAProxy (bez přihlášení): ověřuje i dostupnost DB.
app.MapGet("/health", async (AcsDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "db-unavailable" }, statusCode: 503);
}).AllowAnonymous();

// Přepnutí barevného tématu (cookie → funguje bez ohledu na node).
app.MapPost("/set-theme", async (HttpContext context, AcsDbContext db) =>
{
    var theme = context.Request.Form["theme"].FirstOrDefault() ?? "light";
    context.Response.Cookies.Append("acs-theme", theme, new CookieOptions
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1),
        IsEssential = true,
        HttpOnly = false,
    });

    if (context.User.Identity?.IsAuthenticated == true)
    {
        var userName = context.User.Identity.Name;
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName);
        if (user is not null)
        {
            user.Theme = theme;
            await db.SaveChangesAsync();
        }
    }

    var referer = context.Request.Headers.Referer.FirstOrDefault() ?? "/";
    return Results.LocalRedirect(new Uri(referer, UriKind.RelativeOrAbsolute).IsAbsoluteUri
        ? new Uri(referer).PathAndQuery
        : referer);
}).AllowAnonymous().DisableAntiforgery();

// Schéma patra (obrázek uložený v DB — dostupný z obou HA nodů).
app.MapGet("/floors/{id:int}/schema", async (int id, AcsDbContext db) =>
{
    var floor = await db.Floors.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
    return floor?.SchemaImage is null
        ? Results.NotFound()
        : Results.File(floor.SchemaImage, floor.SchemaContentType ?? "image/png");
});

app.MapRazorPages();

app.Run();

/// <summary>Zpřístupnění pro integrační testy (WebApplicationFactory).</summary>
public partial class Program;
