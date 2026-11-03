using Icaro;
using MongoDB.Driver;

namespace CoursePilot.Host.Modules.Users.Mongo;

public sealed class UsersMongoInitializer(IMongoDatabase database)
{
    public Task Initialize()
    {
        var collection = database.GetCollection<Document<UserDocument>>(MongoUsersRepository.CollectionName);
        var keys = Builders<Document<UserDocument>>.IndexKeys.Ascending(x => x.Value.NormalizedEmail);
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<Document<UserDocument>>(keys, new CreateIndexOptions { Unique = true, Name = "users_normalized_email_unique" }));
    }
}
