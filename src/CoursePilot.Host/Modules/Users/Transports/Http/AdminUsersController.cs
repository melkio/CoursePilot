using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Icaro;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/users")]
public sealed class AdminUsersController(IUsersService users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25, [FromQuery] string? role = null, [FromQuery] string? approval = null)
    {
        UserRole? parsedRole = role is null ? null : UsersController.ParseRole(role);
        TeacherApprovalStatus? parsedApproval = approval switch { null => null, "Pending" => TeacherApprovalStatus.Pending, "Approved" => TeacherApprovalStatus.Approved, _ => throw new ArgumentException("Invalid approval.") };
        var result = await users.List(ActorId(), offset, limit, parsedRole, parsedApproval);
        return Ok(result.Select(x => GetUserModel.From(x.Context, x.User)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id) => Respond(await users.Get(new UserId(id)));

    [HttpPut("{id:guid}/email")]
    public async Task<IActionResult> ChangeEmail(Guid id, ChangeUserEmailModel model) => Respond(await users.Execute(new ChangeUserEmailCommand(ActorId(), new UserId(id), ExpectedVersion(), model.Email)));

    [HttpPut("{id:guid}/role")]
    public async Task<IActionResult> ChangeRole(Guid id, ChangeUserRoleModel model) => Respond(await users.Execute(new ChangeUserRoleCommand(ActorId(), new UserId(id), ExpectedVersion(), UsersController.ParseRole(model.Role))));

    [HttpPost("{id:guid}/approve-teacher")]
    public async Task<IActionResult> Approve(Guid id) => Respond(await users.Execute(new ApproveTeacherCommand(ActorId(), new UserId(id), ExpectedVersion())));

    private UserId ActorId() => new(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException()));

    private int ExpectedVersion()
    {
        var value = Request.Headers.IfMatch.ToString();
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || !int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new ArgumentException("If-Match must be a quoted positive version.");
        return version;
    }

    private IActionResult Respond((ResourceContext Context, User User) result)
    {
        Response.Headers.ETag = $"\"{result.Context.Version}\"";
        return Ok(GetUserModel.From(result.Context, result.User));
    }
}
