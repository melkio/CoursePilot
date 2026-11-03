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
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
public sealed class UsersController(IUsersService users, IAntiforgery antiforgery) : ControllerBase
{
    [AllowAnonymous, HttpGet("antiforgery")]
    [ProducesResponseType(typeof(AntiforgeryTokenModel), StatusCodes.Status200OK)]
    public IActionResult AntiforgeryToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryTokenModel { Token = tokens.RequestToken, Header = tokens.HeaderName });
    }

    [AllowAnonymous, HttpPost("register"), EnableRateLimiting("identity")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register(RegisterUserModel model)
    {
        var role = ParseRole(model.Role);
        var command = new RegisterUserCommand(model.Email, model.Password, role);
        await users.Execute(command);
        var (context, user) = await users.Get(command.Id);
        return Created($"/api/users/{user.Id.Value}", GetUserModel.From(context, user));
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("identity")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginModel model)
    {
        var identity = await users.Search(new AuthenticateUserQuery(model.Email, model.Password));
        if (identity is null) return Problem(statusCode: 401, title: "Invalid credentials.");
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UsersCookieEvents.CreatePrincipal(identity.Id, identity.SessionRevision));
        var (context, user) = await users.Get(identity.Id);
        return Ok(GetUserModel.From(context, user));
    }

    [Authorize, HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [Authorize, HttpGet("me")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> Me() => Detail(ActorId());

    [Authorize, HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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
