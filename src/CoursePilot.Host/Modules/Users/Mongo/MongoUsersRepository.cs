using Icaro;
using MongoDB.Driver;

namespace CoursePilot.Host.Modules.Users.Mongo;

public sealed class MongoUsersRepository(IMongoDatabase database, UserDocumentMapper documentMapper)
    : DefaultMongoRepository<UserId, User, UserDocument>(database, CollectionName, documentMapper), IUsersRepository
{
    public const string CollectionName = "users";

    public new async Task<(ResourceContext Context, User User)> Get(UserId id)
    {
        var result = await base.Get(id);
        if (result.Item1 is null) throw new KeyNotFoundException();
        return result;
    }

    public new async Task<ResourceContext> Upsert(User resource, int? version = null)
    {
        try { return await base.Upsert(resource, version); }
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

    public async Task<(ResourceContext Context, User User)?> FindByEmail(string normalizedEmail)
    {
        var document = await collection.Find(x => x.IsActive && x.Value.NormalizedEmail == normalizedEmail).SingleOrDefaultAsync();
        return document is null ? null : (document.ToResourceContext(), mapper.ToResource(document));
    }

    public async Task<IReadOnlyList<(ResourceContext Context, User User)>> List(int offset, int limit, UserRole? role, TeacherApprovalStatus? approval)
    {
        var filter = Builders<Document<UserDocument>>.Filter.Eq(x => x.IsActive, true);
        if (role.HasValue) filter &= Builders<Document<UserDocument>>.Filter.Eq(x => x.Value.Role, role.Value.ToString());
        if (approval.HasValue) filter &= Builders<Document<UserDocument>>.Filter.Eq(x => x.Value.TeacherApprovalStatus, approval.Value.ToString());
        var documents = await collection.Find(filter).SortBy(x => x.Id).Skip(offset).Limit(limit).ToListAsync();
        return documents.Select(x => (x.ToResourceContext(), mapper.ToResource(x))).ToArray();
    }
}

public sealed class UsersDuplicateEmailException : Exception;
