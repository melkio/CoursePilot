using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

public sealed class UsersCookieEvents(IUsersService users) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var revision = context.Principal?.FindFirstValue("session_revision");
        if (!Guid.TryParse(id, out var guid) || !int.TryParse(revision, out var expected))
        {
            await Reject(context);
            return;
        }
        try
        {
            var (_, user) = await users.Get(new UserId(guid));
            if (user.SessionRevision != expected) { await Reject(context); return; }
            context.ReplacePrincipal(CreatePrincipal(user.Id, user.SessionRevision));
        }
        catch (Exception)
        {
            // Fail closed on missing user or unavailable persistence.
            await Reject(context);
        }
    }

    public static ClaimsPrincipal CreatePrincipal(UserId id, int revision) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.Value.ToString()), new Claim("session_revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
