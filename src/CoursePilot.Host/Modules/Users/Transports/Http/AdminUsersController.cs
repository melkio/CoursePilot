using System.Globalization;
using System.Security.Claims;
using Icaro;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/users")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class AdminUsersController(IUsersService users) : ControllerBase
{
    [HttpPost("search")]
    [ProducesResponseType(typeof(IEnumerable<GetUserModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromBody] PostSearchModel model)
    {
        UserRole? role = model.Role is null ? null : UsersController.ParseRole(model.Role);
        TeacherApprovalStatus? approval = model.Approval switch
        {
            null => null,
            "Pending" => TeacherApprovalStatus.Pending,
            "Approved" => TeacherApprovalStatus.Approved,
            _ => throw new ArgumentException("Invalid approval.")
        };
        var result = await users.List(ActorId(), model.Offset, model.Limit, role, approval);
        return Ok(result.Select(item => GetUserModel.From(item.Context, item.User)));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id)
    {
        var result = await users.Get(new UserId(id));
        return Respond(result);
    }

    [HttpPut("{id:guid}/email")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    public async Task<IActionResult> ChangeEmail(Guid id, [FromBody] ChangeUserEmailModel model)
    {
        var command = new ChangeUserEmailCommand(ActorId(), new UserId(id), ExpectedVersion(), model.Email);
        await users.Execute(command);
        var result = await users.Get(command.TargetId);
        return Respond(result);
    }

    [HttpPut("{id:guid}/role")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeUserRoleModel model)
    {
        var command = new ChangeUserRoleCommand(ActorId(), new UserId(id), ExpectedVersion(), UsersController.ParseRole(model.Role));
        await users.Execute(command);
        var result = await users.Get(command.TargetId);
        return Respond(result);
    }

    [HttpPost("{id:guid}/approve-teacher")]
    [ProducesResponseType(typeof(GetUserModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    public async Task<IActionResult> Approve(Guid id)
    {
        var command = new ApproveTeacherCommand(ActorId(), new UserId(id), ExpectedVersion());
        await users.Execute(command);
        var result = await users.Get(command.TargetId);
        return Respond(result);
    }

    private UserId ActorId()
    {
        return new UserId(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException()));
    }

    private int ExpectedVersion()
    {
        var value = Request.Headers.IfMatch.ToString();
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' ||
            !int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("If-Match must be a quoted positive version.");
        }
        // Icaro.Http 1.0 accepts an unquoted integer; retain the public quoted-ETag contract.
        IHeaderDictionary headers = new HeaderDictionary { ["If-Match"] = value[1..^1] };
        var version = headers.TryGetResourceVersion();
        if (!version.HasValue || version.Value < 1)
        {
            throw new ArgumentException("If-Match must be a quoted positive version.");
        }
        return version.Value;
    }

    private IActionResult Respond((ResourceContext Context, User User) result)
    {
        Response.Headers.ETag = $"\"{result.Context.Version}\"";
        return Ok(GetUserModel.From(result.Context, result.User));
    }
}
