using Icaro;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed record GetUserModel(Guid Id, string Email, string Role, string? TeacherApprovalStatus, int Version)
{
    public static GetUserModel From(ResourceContext context, User user) => new(user.Id.Value, user.Email, user.Role.ToString(), user.TeacherApprovalStatus?.ToString(), context.Version);
}
