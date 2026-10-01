using System.Security.Cryptography;
using System.Threading.RateLimiting;
using CampusPass.Application;
using CampusPass.Infrastructure;
using CampusPass.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------- Data: SQLite file in App_Data (swap the provider for SQL Server/PostgreSQL in production at scale) ----------
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDir);
var connection = cfg.GetConnectionString("CampusPass") ?? $"Data Source={Path.Combine(dataDir, "campuspass.db")}";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
builder.Services.AddScoped<IAppDb>(sp => sp.GetRequiredService<AppDbContext>());

// Keys that protect login cookies survive restarts (people stay signed in).
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("CampusPass");

// ---------- Business rules ----------
builder.Services.AddSingleton<IClock>(new CampusClock(CampusClock.Resolve(cfg["TimeZone"] ?? "Africa/Nairobi")));
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<QueueService>();
builder.Services.AddScoped<ClearanceService>();
builder.Services.AddScoped<FeeImportService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<DemoService>();
builder.Services.AddSingleton(new SetupCode(Convert.ToHexString(RandomNumberGenerator.GetBytes(6))));

// ---------- Web ----------
builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    o.Filters.Add<SetupAndPasswordGate>();
    o.Filters.Add<RuleExceptionFilter>();
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/Denied";
    o.Cookie.Name = "campuspass";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
});
builder.Services.AddAuthorization();

// Behind a reverse proxy / tunnel on this machine, read the real client IP and https scheme.
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

static string Client(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(Client(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("verify", c => RateLimitPartition.GetFixedWindowLimiter(Client(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// ---------- Database schema (migrations) + first-run setup code ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    var setup = scope.ServiceProvider.GetRequiredService<SetupService>();

    // Demo mode (set "Demo": true, or the Demo env var) gives a running, fully populated system with no manual setup —
    // for showing the project. It never triggers unless deliberately switched on, so a shipped app carries no test data.
    if (cfg.GetValue<bool>("Demo"))
    {
        var demo = scope.ServiceProvider.GetRequiredService<DemoService>();
        if (!await demo.HasDataAsync())
        {
            await demo.LoadAsync();
            app.Logger.LogWarning("CampusPass DEMO loaded. Password {Password}. Logins: admin; students EB1/0001/23 .. EB6/0006/23; staff 'STAFF 1' .. 'STAFF 5'.", DemoService.Password);
        }
    }
    else if (!await setup.IsCompleteAsync())
        app.Logger.LogWarning("CampusPass is not set up yet. Open /Setup and enter this one-time setup code: {Code}", app.Services.GetRequiredService<SetupCode>().Value);
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; form-action 'self'; base-uri 'self'";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
