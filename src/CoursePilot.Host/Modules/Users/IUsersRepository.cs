using Icaro;

namespace CoursePilot.Host.Modules.Users;

public abstract record UsersQuery;
public sealed record UsersByEmailQuery(string NormalizedEmail) : UsersQuery;
public sealed record UsersListQuery(int Offset, int Limit, UserRole? Role, TeacherApprovalStatus? Approval) : UsersQuery;

public interface IUsersRepository
{
    Task<(ResourceContext Context, User User)> Get(UserId id);
    Task<ResourceContext> Upsert(User resource, int? version = null);
    Task<IReadOnlyList<(ResourceContext Context, User User)>> Search(UsersQuery query);
}
