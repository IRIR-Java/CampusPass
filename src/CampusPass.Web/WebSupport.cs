using System.Security.Claims;
using CampusPass.Application;
using CampusPass.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace CampusPass.Web;

/// <summary>One-time code printed in the server log on a fresh install; needed to create the first admin.</summary>
public record SetupCode(string Value);

public static class Auth
{
    public const string MustChange = "must_change";

    public static Task SignInAsync(HttpContext http, UserAccount u)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, u.DisplayName),
            new(ClaimTypes.Role, u.Role.ToString()),
            new("username", u.Username)
        };
        if (u.MustChangePassword) claims.Add(new Claim(MustChange, "1"));
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }

    public static int UserId(this ClaimsPrincipal p) => int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public static string HomeFor(Role role) => role switch
    {
        Role.Student => "/Student",
        Role.Staff => "/Staff",
        Role.Admin => "/Admin",
        _ => "/"
    };

    /// <summary>The signed-in account, freshly loaded (disabled accounts are signed out).</summary>
    public static async Task<UserAccount?> CurrentAsync(this Controller c, IAppDb db)
    {
        var id = c.User.UserId();
        var u = await db.Users.Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == id);
        return u is null || u.Disabled ? null : u;
    }

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => char.IsLetter(p[0])).ToArray();
        return parts.Length switch { 0 => "?", 1 => parts[0][..1].ToUpperInvariant(), _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant() };
    }
}

/// <summary>Before setup only /Setup works; accounts with a one-time password must choose their own first.</summary>
public class SetupAndPasswordGate(SetupService setup) : IAsyncActionFilter
{
    private static bool? _done;

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var controller = ctx.RouteData.Values["controller"] as string;
        var action = ctx.RouteData.Values["action"] as string;

        if (_done != true)
        {
            _done = await setup.IsCompleteAsync();
            if (_done != true && controller != "Setup" && !(controller == "Home" && action == "Error"))
            {
                ctx.Result = new RedirectResult("/Setup");
                return;
            }
        }

        var user = ctx.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true && user.HasClaim(Auth.MustChange, "1") &&
            !(controller == "Account" && action is "ChangePassword" or "Logout"))
        {
            ctx.Result = new RedirectResult("/Account/ChangePassword");
            return;
        }
        await next();
    }
}

/// <summary>Business-rule messages ("Finish ICT-004 first") go back to the previous page as a friendly banner.</summary>
public class RuleExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext ctx)
    {
        if (ctx.Exception is not RuleException ex) return;
        var tempData = ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>().GetTempData(ctx.HttpContext);
        tempData["Error"] = ex.Message;
        var referer = ctx.HttpContext.Request.Headers.Referer.ToString();
        var back = Uri.TryCreate(referer, UriKind.Absolute, out var u) && u.Host == ctx.HttpContext.Request.Host.Host ? u.PathAndQuery : "/";
        ctx.Result = new RedirectResult(back);
        ctx.ExceptionHandled = true;
    }
}

public static class Qr
{
    /// <summary>Server-rendered SVG QR code (no JavaScript, no external service).</summary>
    public static string Svg(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(new System.Drawing.Size(8, 8), "#0b2e1d", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}

public static class Fmt
{
    public static string Money(decimal v) => "KES " + v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string Window(DateTime start, DateTime end) => $"{start:ddd d MMM}, {start:h:mm} – {end:h:mm tt}";

    public static string Ago(DateTime t, DateTime now)
    {
        var s = now - t;
        return s.TotalMinutes < 1 ? "just now" : s.TotalMinutes < 60 ? $"{(int)s.TotalMinutes} min ago" : s.TotalHours < 24 ? $"{(int)s.TotalHours} h ago" : t.ToString("d MMM");
    }

    public static (string Label, string Css) Status(TicketStatus s) => s switch
    {
        TicketStatus.Waiting => ("Waiting", "pill-amber"),
        TicketStatus.Called => ("Called", "pill-blue"),
        TicketStatus.Serving => ("In service", "pill-green"),
        TicketStatus.Completed => ("Served", "pill-green"),
        TicketStatus.NoShow => ("Missed", "pill-grey"),
        TicketStatus.Cancelled => ("Cancelled", "pill-grey"),
        _ => (s.ToString(), "pill-grey")
    };
}
