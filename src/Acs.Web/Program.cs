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
builder.Services.AddScoped<DcLocator>();
builder.Services.AddScoped<LdapAuthenticator>();
builder.Services.AddScoped<UserAuthenticationService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddHttpClient<WinPakClient>();

// Synchronizace číselníků (ruční tlačítka + automatický plánovač s DB zámkem).
builder.Services.AddScoped<Acs.Infrastructure.Sync.ReaderSyncService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.EmployeeSyncService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.AccessSyncService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.CardSyncService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.AutoAssignmentService>();
builder.Services.AddScoped<Acs.Infrastructure.Automation.AutomationService>();
builder.Services.AddScoped<Acs.Infrastructure.Automation.HealthCheckService>();
builder.Services.AddScoped<Acs.Infrastructure.Import.PlanImportService>();
builder.Services.AddScoped<Acs.Infrastructure.Import.EkvReaderImportService>();
builder.Services.AddScoped<Acs.Infrastructure.Plans.PlanGenerationService>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.EmployeeSourceFactory>();
builder.Services.AddScoped<Acs.Infrastructure.Sync.LdapDiagnosticsService>();
builder.Services.AddHttpClient(nameof(Acs.Infrastructure.Sync.ApiEmployeeSource));
builder.Services.AddSingleton<Acs.Infrastructure.Sync.SyncJobRunner>();
builder.Services.AddHostedService<Acs.Infrastructure.Sync.SyncScheduler>();

// Schvalovací workflow, fronta správce karet a e-mailové notifikace.
builder.Services.AddScoped<Acs.Infrastructure.Notifications.INotificationService,
    Acs.Infrastructure.Notifications.EmailNotificationService>();
builder.Services.AddScoped<Acs.Infrastructure.Notifications.AttentionService>();
builder.Services.AddScoped<Acs.Infrastructure.Workflow.ReaderGroupService>();
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

// Antiforgery i přes hlavičku — pro JSON POSTy z interaktivního editoru plánů.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

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
    // Volitelné počáteční heslo admina z konfigurace (ACS_BOOTSTRAP_ADMIN_PASSWORD).
    var bootstrapAdminPassword = builder.Configuration["Admin:BootstrapPassword"]
        ?? Environment.GetEnvironmentVariable("ACS_BOOTSTRAP_ADMIN_PASSWORD");
    await DatabaseInitializer.InitializeAsync(db, initLogger, bootstrapAdminPassword);
}

// ---------- Jednorázový import z DPS plánů (CLI) ----------
// Použití: Acs.Web --import-plan <rooms.json> [--building MOC] [--dry-run]
//          [--prefer-corridor]   (výchozí je přiřazovat čtečky k nechodbovým místnostem)
if (args.Contains("--import-plan"))
{
    var idx = Array.IndexOf(args, "--import-plan");
    var path = idx + 1 < args.Length ? args[idx + 1] : null;
    if (path is null || !File.Exists(path))
    {
        Console.Error.WriteLine($"Soubor s daty nenalezen: {path ?? "(nezadán)"}");
        return 2;
    }

    var buildingIdx = Array.IndexOf(args, "--building");
    var buildingName = buildingIdx >= 0 && buildingIdx + 1 < args.Length ? args[buildingIdx + 1] : "MOC";

    using var importScope = app.Services.CreateScope();
    var importer = importScope.ServiceProvider
        .GetRequiredService<Acs.Infrastructure.Import.PlanImportService>();
    await using var planStream = File.OpenRead(path);
    var plan = Acs.Infrastructure.Import.PlanImportService.Parse(planStream);
    var importResult = await importer.ImportAsync(
        plan, buildingName,
        dryRun: args.Contains("--dry-run"),
        preferNonCorridor: !args.Contains("--prefer-corridor"),
        userName: "cli");
    Console.WriteLine(importResult);
    return 0;
}

// ---------- Import čteček z tabulky EKV (CLI) ----------
// Použití: Acs.Web --import-readers <tabulka.xlsx> [--building MOC] [--dry-run] [--keep-unmatched]
//          [--positions import/moc/ekv-readers.json]   (polohy z výkresů EKV, viz extract_ekv.py)
// Tabulka „čtečky EKV“ z dokumentace skutečného provedení; jde nahrát dveřní
// i výtahovou. Čtečky z výkresů se sjednotí podle rozvaděče a místnosti;
// co protějšek nemá, se deaktivuje (--keep-unmatched to vypne).
if (args.Contains("--import-readers"))
{
    var idx = Array.IndexOf(args, "--import-readers");
    var path = idx + 1 < args.Length ? args[idx + 1] : null;
    if (path is null || !File.Exists(path))
    {
        Console.Error.WriteLine($"Soubor s tabulkou nenalezen: {path ?? "(nezadán)"}");
        return 2;
    }

    var buildingIdx = Array.IndexOf(args, "--building");
    var buildingName = buildingIdx >= 0 && buildingIdx + 1 < args.Length ? args[buildingIdx + 1] : "MOC";

    using var readersScope = app.Services.CreateScope();
    var readerImporter = readersScope.ServiceProvider
        .GetRequiredService<Acs.Infrastructure.Import.EkvReaderImportService>();
    await using var tableStream = File.OpenRead(path);
    var tableRows = Acs.Infrastructure.Import.EkvReaderImportService.Parse(tableStream);

    Dictionary<string, List<Acs.Infrastructure.Import.EkvPosition>>? readerPositions = null;
    var positionsIdx = Array.IndexOf(args, "--positions");
    if (positionsIdx >= 0 && positionsIdx + 1 < args.Length)
    {
        await using var positionsStream = File.OpenRead(args[positionsIdx + 1]);
        readerPositions = Acs.Infrastructure.Import.EkvReaderImportService.ParsePositions(positionsStream);
    }

    var readersResult = await readerImporter.ImportAsync(
        tableRows, buildingName,
        dryRun: args.Contains("--dry-run"),
        deactivateUnmatched: !args.Contains("--keep-unmatched"),
        userName: "cli",
        positions: readerPositions);
    Console.WriteLine(readersResult);
    return 0;
}

// ---------- Výpis atributů účtu z AD (CLI) ----------
// Použití: Acs.Web --ldap-dump <přihlašovací jméno | osobní číslo | příjmení>
//          [--server dc01.domena.local] [--port 389] [--no-ssl]
//          [--base-dn "DC=domena,DC=local"] [--bind-user ucet@domena.local]
//          [--attribute employeeNumber]
//
// Vypíše, co o účtu vrací doménový řadič, a co z toho ACS sestaví. Slouží
// k dohledání, ze kterého atributu brát osobní číslo.
//
// Bez --server se použije nastavení z databáze (Nastavení → Active Directory).
// S --server se jde mimo nastavení — hodí se k ověření proti řadiči, na který
// se ACS ještě nenastavil, nebo když LDAP vede přes SSH tunel z jiné sítě.
// Heslo se bere z proměnné ACS_LDAP_BIND_PASSWORD; v argumentu by ho viděl
// každý, kdo si vypíše běžící procesy.
if (args.Contains("--ldap-dump"))
{
    var idx = Array.IndexOf(args, "--ldap-dump");
    var query = idx + 1 < args.Length ? args[idx + 1] : null;
    if (string.IsNullOrWhiteSpace(query))
    {
        Console.Error.WriteLine("Zadejte přihlašovací jméno, osobní číslo nebo příjmení.");
        return 2;
    }

    using var ldapScope = app.Services.CreateScope();
    var diagnostics = ldapScope.ServiceProvider
        .GetRequiredService<Acs.Infrastructure.Sync.LdapDiagnosticsService>();

    string? Arg(string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    Acs.Infrastructure.Sync.LdapConnectionOptions? ldapOptions = null;
    if (Arg("--server") is { } ldapServer)
    {
        var useSsl = !args.Contains("--no-ssl");
        var password = Environment.GetEnvironmentVariable("ACS_LDAP_BIND_PASSWORD");
        if (Arg("--base-dn") is not { } baseDn || Arg("--bind-user") is not { } bindUser
            || string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine(
                "Při zadaném --server je potřeba i --base-dn, --bind-user"
                + " a heslo v proměnné ACS_LDAP_BIND_PASSWORD.");
            return 2;
        }

        ldapOptions = new Acs.Infrastructure.Sync.LdapConnectionOptions(
            Server: ldapServer,
            Port: int.TryParse(Arg("--port"), out var ldapPort) ? ldapPort : useSsl ? 636 : 389,
            UseSsl: useSsl,
            BaseDn: baseDn,
            BindUser: bindUser,
            BindPassword: password,
            PersonalNumberAttributes: Arg("--attribute") is { } attribute
                ? Acs.Infrastructure.Sync.LdapAttributes.ParseAttributeList(attribute,
                    Acs.Infrastructure.Sync.LdapEmployeeSource.DefaultPersonalNumberAttributes)
                : null);
    }

    try
    {
        var dump = await diagnostics.DumpAsync(query, ldapOptions);
        Console.WriteLine(dump);
        Console.WriteLine($"Base DN: {dump.BaseDn}");
        Console.WriteLine($"Filtr:   {dump.Filter}");
        Console.WriteLine($"Osobní číslo se bere z: {string.Join(" -> ", dump.PersonalNumberAttributes)}");

        foreach (var entry in dump.Entries)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {entry.Dn} ===");
            if (entry.Mapped is { } mapped)
            {
                Console.WriteLine($"ACS uloží: osobní číslo „{mapped.PersonalNumber ?? "-"}“"
                    + $" (z {entry.PersonalNumberFrom ?? "žádného atributu"}), účet {mapped.ExternalId},"
                    + $" {mapped.FirstName} {mapped.LastName}, oddělení {mapped.Department ?? "-"}");
            }
            else
            {
                Console.WriteLine("ACS účet přeskočí — chybí sAMAccountName.");
            }

            Console.WriteLine("--- atributy z AD ---");
            foreach (var attribute in entry.Attributes)
            {
                var use = attribute.MapsTo is null ? "" : $"   [{attribute.MapsTo}]";
                Console.WriteLine($"{attribute.Name,-32} {attribute.Joined}{use}");
            }
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Výpis z AD se nezdařil: {ex.Message}");
        return 2;
    }
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
return 0;

/// <summary>Zpřístupnění pro integrační testy (WebApplicationFactory).</summary>
public partial class Program;
