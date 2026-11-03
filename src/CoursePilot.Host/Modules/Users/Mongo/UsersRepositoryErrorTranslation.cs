using Icaro;
using MongoDB.Driver;

namespace CoursePilot.Host.Modules.Users.Mongo;

// Translate Icaro/Mongo adapter results at the boundary without shadowing its CRUD implementation.
public sealed class UsersRepositoryErrorTranslation(MongoUsersRepository repository) : IUsersRepository
{
    public async Task<(ResourceContext Context, User User)> Get(UserId id)
    {
        var result = await repository.Get(id);
        if (result.Item1 is null)
        {
            throw new KeyNotFoundException();
        }
        return result;
    }

    public async Task<ResourceContext> Upsert(User resource, int? version = null)
    {
        try
        {
            return await repository.Upsert(resource, version);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new UsersDuplicateEmailException();
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            throw new UsersDuplicateEmailException();
        }
        catch (InvalidOperationException) when (version.HasValue)
        {
            throw new UsersVersionException();
        }
    }

    public Task<IReadOnlyList<(ResourceContext Context, User User)>> Search(UsersQuery query)
    {
        return repository.Search(query);
    }
}
