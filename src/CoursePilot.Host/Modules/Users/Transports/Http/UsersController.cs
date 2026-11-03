using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

[ApiController]
[Route("api/users")]
public sealed class UsersController(IUsersService users, IAntiforgery antiforgery) : ControllerBase
{
    [AllowAnonymous, HttpGet("antiforgery")]
    public IActionResult AntiforgeryToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken, header = tokens.HeaderName });
    }

    [AllowAnonymous, HttpPost("register"), EnableRateLimiting("identity")]
    public async Task<IActionResult> Register(RegisterUserModel model)
    {
        var role = ParseRole(model.Role);
        var (context, user) = await users.Execute(new RegisterUserCommand(model.Email, model.Password, role));
        return Created($"/api/users/{user.Id.Value}", GetUserModel.From(context, user));
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("identity")]
    public async Task<IActionResult> Login(LoginModel model)
    {
        var identity = await users.Execute(new AuthenticateUserCommand(model.Email, model.Password));
        if (identity is null) return Problem(statusCode: 401, title: "Invalid credentials.");
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UsersCookieEvents.CreatePrincipal(identity.Id, identity.SessionRevision));
        var (context, user) = await users.Get(identity.Id);
        return Ok(GetUserModel.From(context, user));
    }

    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [Authorize, HttpGet("me")]
    public Task<IActionResult> Me() => Detail(ActorId());

    [Authorize, HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var actor = ActorId();
        if (id != actor)
        {
            var (_, current) = await users.Get(new UserId(actor));
            if (current.Role != UserRole.Admin) return Forbid();
        }
        var (context, user) = await users.Get(new UserId(id));
        Response.Headers.ETag = $"\"{context.Version}\"";
        return Ok(GetUserModel.From(context, user));
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
    internal static UserRole ParseRole(string value) => value switch
    {
        "Student" => UserRole.Student,
        "Teacher" => UserRole.Teacher,
        "Admin" => UserRole.Admin,
        _ => throw new ArgumentException("Invalid role.")
    };
}
