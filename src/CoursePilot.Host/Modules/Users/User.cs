using Icaro;

namespace CoursePilot.Host.Modules.Users;

public sealed record UserId(Guid Value) : ResourceId(Value);

public sealed record User(UserId Id, string Email, string NormalizedEmail, string PasswordHash,
    UserRole Role, TeacherApprovalStatus? TeacherApprovalStatus, int SessionRevision) : Resource<UserId>(Id);
