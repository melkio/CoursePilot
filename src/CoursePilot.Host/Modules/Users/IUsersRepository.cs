using Icaro;

namespace CoursePilot.Host.Modules.Users;

public interface IUsersRepository
{
    Task<(ResourceContext Context, User User)> Get(UserId id);
    Task<ResourceContext> Upsert(User resource, int? version = null);
    Task<(ResourcesContext Context, ResourceEnvelope<User>[] Users)> Search(UsersQuery query);
}
