using Icaro;

namespace CoursePilot.Host.Modules.Users.Mongo;

// Icaro.MongoDB 1.0.0 exposes a two-parameter mapper, not the three-parameter API in the plan.
public sealed class UserDocumentMapper : IDocumentMapper<User, UserDocument>
{
    public UserDocument ToDocument(User resource) => new()
    {
        Email = resource.Email,
        NormalizedEmail = resource.NormalizedEmail,
        PasswordHash = resource.PasswordHash,
        Role = resource.Role.ToString(),
        TeacherApprovalStatus = resource.TeacherApprovalStatus?.ToString(),
        SessionRevision = resource.SessionRevision
    };

    public User ToResource(Document<UserDocument> document) => new(
        new UserId(Guid.Parse(document.Id)), document.Value.Email, document.Value.NormalizedEmail,
        document.Value.PasswordHash, Enum.Parse<UserRole>(document.Value.Role),
        document.Value.TeacherApprovalStatus is null ? null : Enum.Parse<TeacherApprovalStatus>(document.Value.TeacherApprovalStatus),
        document.Value.SessionRevision);
}
