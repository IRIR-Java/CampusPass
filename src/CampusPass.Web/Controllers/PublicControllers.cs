using System.Diagnostics;
using CampusPass.Application;
using CampusPass.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CampusPass.Web.Controllers;

public class HomeController(IAppDb db) : Controller
{
    public async Task<IActionResult> Index()
    {
        if (User.Identity?.IsAuthenticated == true && Enum.TryParse<Role>(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var role))
            return Redirect(Auth.HomeFor(role));
        ViewData["Institution"] = (await db.Settings.FirstOrDefaultAsync())?.InstitutionName ?? "";
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(model: Activity.Current?.Id ?? HttpContext.TraceIdentifier);
}

public class SetupForm
{
    public string Code { get; set; } = "";
    public string Institution { get; set; } = "";
    public string Semester { get; set; } = "";
    public DateOnly PassValidUntil { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddMonths(3));
    public string AdminUsername { get; set; } = "admin";
    public string AdminName { get; set; } = "";
    public string Password { get; set; } = "";
}

[AllowAnonymous]
public class SetupController(SetupService setup, SetupCode code) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index() => await setup.IsCompleteAsync() ? Redirect("/") : View(new SetupForm());

    [HttpPost, EnableRateLimiting("login")]
    public async Task<IActionResult> Index(SetupForm form)
    {
        if (await setup.IsCompleteAsync()) return Redirect("/");
        if (!string.Equals(form.Code?.Trim(), code.Value, StringComparison.OrdinalIgnoreCase))
        {
            ViewData["Error"] = "That setup code is not correct. It is printed in the server log.";
            return View(form);
        }
        try
        {
            await setup.CompleteAsync(form.Institution, form.Semester, form.PassValidUntil, form.AdminUsername, form.AdminName, form.Password);
        }
        catch (RuleException ex)
        {
            ViewData["Error"] = ex.Message;
            return View(form);
        }
        TempData["Success"] = "CampusPass is ready. Sign in with the admin account you just created.";
        return Redirect("/Account/Login");
    }
}

[AllowAnonymous]
public class AccountController(AccountService accounts, IAppDb db) : Controller
{
    [HttpGet]
    public IActionResult Login(string? returnUrl, bool first = false)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        ViewData["ReturnUrl"] = returnUrl;
        ViewData["First"] = first;
        return View();
    }

    [HttpPost, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        ViewData["Username"] = username;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            ViewData["Error"] = "Enter your username and password.";
            return View();
        }
        var r = await accounts.LoginAsync(username, password);
        if (r.Outcome != LoginOutcome.Ok)
        {
            ViewData["Error"] = r.Outcome == LoginOutcome.Locked
                ? "Too many attempts. Wait 5 minutes and try again."
                : "Wrong username or password.";   // never say which one
            return View();
        }
        await Auth.SignInAsync(HttpContext, r.User!);
        if (r.User!.MustChangePassword) return Redirect("/Account/ChangePassword");
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : Redirect(Auth.HomeFor(r.User.Role));
    }

    [HttpPost, Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/Account/Login");   // straight back to sign-in makes switching accounts one step
    }

    [HttpGet, Authorize]
    public IActionResult ChangePassword() => View();

    [HttpPost, Authorize, EnableRateLimiting("login")]
    public async Task<IActionResult> ChangePassword(string current, string next, string confirm)
    {
        if (next != confirm) { ViewData["Error"] = "The two new passwords don't match."; return View(); }
        try { await accounts.ChangePasswordAsync(User.UserId(), current, next); }
        catch (RuleException ex) { ViewData["Error"] = ex.Message; return View(); }

        var user = await this.CurrentAsync(db);
        if (user is null) return Redirect("/");
        await Auth.SignInAsync(HttpContext, user);   // re-issue the cookie without the "must change" flag
        TempData["Success"] = "Password saved.";
        return Redirect(Auth.HomeFor(user.Role));
    }

    [HttpGet]
    public IActionResult Denied() => View();
}

[AllowAnonymous]
public class VerifyController(ClearanceService clearance, IClock clock) : Controller
{
    /// <summary>What the guard's phone opens after scanning a gatepass QR. No login, minimum personal data.</summary>
    [HttpGet, EnableRateLimiting("verify")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Index(string? t)
    {
        ViewData["CheckedAt"] = clock.Now;
        return View(string.IsNullOrWhiteSpace(t) ? null : await clearance.VerifyAsync(t));
    }
}

[Authorize]
public class NotificationsController(NotificationService notify, IAppDb db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var list = await notify.ListAsync(User.UserId());
        await notify.MarkAllReadAsync(User.UserId());
        ViewData["Role"] = (await this.CurrentAsync(db))?.Role ?? Role.Student;
        return View(list);
    }
}
