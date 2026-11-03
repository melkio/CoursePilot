using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

public sealed record UsersRoleRequirement(UserRole Role, bool Approved = false) : IAuthorizationRequirement;

public sealed class UsersAuthorizationHandler(IUsersService users) : AuthorizationHandler<UsersRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, UsersRoleRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return;
        try
        {
            var (_, user) = await users.Get(new UserId(id));
            if (user.Role == requirement.Role && (!requirement.Approved || user.TeacherApprovalStatus == TeacherApprovalStatus.Approved)) context.Succeed(requirement);
        }
        catch (Exception) { /* Failure is closed if the user or database is unavailable. */ }
    }
}
