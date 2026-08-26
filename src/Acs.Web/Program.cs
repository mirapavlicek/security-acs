using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Auth;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.WinPak;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
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
    await DatabaseInitializer.InitializeAsync(db);
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
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
