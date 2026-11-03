namespace CoursePilot.Host.Modules.Users.Mongo;

public sealed class UserDocument
{
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? TeacherApprovalStatus { get; set; }
    public int SessionRevision { get; set; }
}
