using Icaro;
using MongoDB.Driver;

namespace CoursePilot.Host.Modules.Users.Mongo;

public sealed class MongoUsersRepository(IMongoDatabase database, IDocumentMapper<User, UserDocument> documentMapper)
    : DefaultMongoRepository<UserId, User, UserDocument>(database, CollectionName, documentMapper), IUsersRepository
{
    public const string CollectionName = "users";

    public async Task<IReadOnlyList<(ResourceContext Context, User User)>> Search(UsersQuery query)
    {
        var builder = Builders<Document<UserDocument>>.Filter;
        var filter = query switch
        {
            UsersByEmailQuery email => builder.Eq(document => document.Value.NormalizedEmail, email.NormalizedEmail),
            UsersListQuery list => BuildListFilter(list),
            _ => throw new ArgumentException("Unknown users query.")
        };
        var (offset, limit) = query switch
        {
            UsersByEmailQuery => (0, 1),
            UsersListQuery list when list.Offset >= 0 && list.Limit is >= 1 and <= 100 && list.Offset <= int.MaxValue - list.Limit => (list.Offset, list.Limit),
            _ => throw new ArgumentException("Invalid pagination.")
        };

        // Icaro pages are 1-based; a non-aligned offset can span at most two pages.
        var page = offset / limit + 1;
        var remainder = offset % limit;
        var (_, first) = await ExecuteQuery(filter, page: page, size: limit);
        var results = first.Skip(remainder).ToList();
        if (remainder != 0 && first.Length == limit && results.Count < limit)
        {
            var (_, second) = await ExecuteQuery(filter, page: page + 1, size: limit);
            results.AddRange(second.Take(limit - results.Count));
        }
        return results.Select(item => (item.Context, item.Resource)).ToArray();
    }

    private static FilterDefinition<Document<UserDocument>> BuildListFilter(UsersListQuery query)
    {
        var builder = Builders<Document<UserDocument>>.Filter;
        var filter = builder.Empty;
        if (query.Role.HasValue)
        {
            filter &= builder.Eq(document => document.Value.Role, query.Role.Value.ToString());
        }
        if (query.Approval.HasValue)
        {
            filter &= builder.Eq(document => document.Value.TeacherApprovalStatus, query.Approval.Value.ToString());
        }
        return filter;
    }
}
