using Icaro;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed class GetUserModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? TeacherApprovalStatus { get; set; }
    public int Version { get; set; }

    public static GetUserModel From(ResourceContext context, User user)
    {
        return new GetUserModel
        {
            Id = user.Id.Value,
            Email = user.Email,
            Role = user.Role.ToString(),
            TeacherApprovalStatus = user.TeacherApprovalStatus?.ToString(),
            Version = context.Version
        };
    }
}
