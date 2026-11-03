using Icaro;

namespace CoursePilot.Host.Modules.Users;

public interface IUsersRepository
{
    Task<(ResourceContext Context, User User)> Get(UserId id);
    Task<ResourceContext> Upsert(User resource, int? version = null);
    Task<(ResourceContext Context, User User)?> FindByEmail(string normalizedEmail);
    Task<IReadOnlyList<(ResourceContext Context, User User)>> List(int offset, int limit, UserRole? role, TeacherApprovalStatus? approval);
}
