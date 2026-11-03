using CoursePilot.Host.Modules.Users;
using CoursePilot.Host.Modules.Users.Identity;
using CoursePilot.Host.Modules.Users.Mongo;
using Icaro;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CoursePilot.Tests.Modules.Users.Mongo;

public sealed class MongoUsersRepositoryTests
{
    private MongoClient client = null!; // Initialized by NUnit before each test.
    private IMongoDatabase database = null!; // Initialized by NUnit before each test.
    private IUsersRepository repository = null!; // Initialized by NUnit before each test.
    private UsersService service = null!; // Initialized by NUnit before each test.
    private string name = string.Empty;

    [SetUp]
    public async Task Setup()
    {
        client = new MongoClient(Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000");
        name = "coursepilot_test_" + Guid.NewGuid().ToString("N");
        database = client.GetDatabase(name);
        await new UsersMongoInitializer(database).Initialize();
        repository = new MongoUsersRepository(database, new UserDocumentMapper());
        service = new UsersService(repository, new AspNetUsersPasswordHasher(), new NullUsersNotifier());
    }

    [TearDown]
    public async Task Cleanup()
    {
        try { if (!string.IsNullOrEmpty(name)) await client.DropDatabaseAsync(name); }
        finally { client?.Dispose(); }
    }

    [Test]
    public async Task AC1_UniqueNormalizedEmailAndDocumentRoundTripSurviveNewRepository()
    {
        var (_, user) = await service.ExecuteAndGet(new RegisterUserCommand("Student@Example.it", "password123", UserRole.Student));
        Assert.ThrowsAsync<MongoWriteException>(() => service.Execute(new RegisterUserCommand("student@example.IT", "password456", UserRole.Student)));
        var fresh = new MongoUsersRepository(database, new UserDocumentMapper());
        var (context, loaded) = await fresh.Get(user.Id);
        Assert.That(loaded, Is.EqualTo(user));
        Assert.That(context.Version, Is.EqualTo(1));
        var raw = await database.GetCollection<BsonDocument>(MongoUsersRepository.CollectionName).Find(new BsonDocument()).SingleAsync();
        Assert.That(raw["Value"]["NormalizedEmail"].AsString, Is.EqualTo("STUDENT@EXAMPLE.IT"));
        Assert.That(raw["Value"]["Role"].AsString, Is.EqualTo("Student"));
    }

    [Test]
    public async Task AC2_MongoVersionRejectsLostUpdateAndFiltersOnServer()
    {
        var (_, admin) = await service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await service.ExecuteAndGet(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        var (changedContext, _) = await service.ExecuteAndGet(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        Assert.ThrowsAsync<InvalidOperationException>(() => repository.Upsert(teacher with { Role = UserRole.Student, TeacherApprovalStatus = null }, context.Version));
        var (current, loaded) = await repository.Get(teacher.Id);
        Assert.That(current.Version, Is.EqualTo(changedContext.Version));
        Assert.That(loaded.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Approved));
        var teachers = await repository.Search(new UsersListQuery(0, 1, UserRole.Teacher, TeacherApprovalStatus.Approved));
        Assert.That(teachers.Users.Select(x => x.Resource.Id), Is.EquivalentTo(new[] { teacher.Id }));
    }

    [Test]
    public async Task AC2_FilteredListIsPagedInMongo()
    {
        var (_, admin) = await service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        for (var i = 0; i < 3; i++)
            await service.Execute(new RegisterUserCommand($"teacher{i}@example.it", "password123", UserRole.Teacher));
        await service.Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        var first = await service.Search(new SearchUsersQuery(admin.Id, 0, 2, UserRole.Teacher, TeacherApprovalStatus.Pending));
        var second = await service.Search(new SearchUsersQuery(admin.Id, 2, 2, UserRole.Teacher, TeacherApprovalStatus.Pending));
        Assert.That(first.Context.Total, Is.EqualTo(3));
        Assert.That(second.Context.Total, Is.EqualTo(3));
        Assert.That(first.Users.Length, Is.EqualTo(2));
        Assert.That(second.Users.Length, Is.EqualTo(1));
        Assert.That(first.Users.Select(x => x.Resource.Id).Intersect(second.Users.Select(x => x.Resource.Id)), Is.Empty);
        Assert.That(first.Users.Concat(second.Users).All(x => x.Resource.Role == UserRole.Teacher && x.Resource.TeacherApprovalStatus == TeacherApprovalStatus.Pending), Is.True);
        var all = await service.Search(new SearchUsersQuery(admin.Id, 0, 3, UserRole.Teacher, TeacherApprovalStatus.Pending));
        var unaligned = await service.Search(new SearchUsersQuery(admin.Id, 1, 2, UserRole.Teacher, TeacherApprovalStatus.Pending));
        Assert.That(unaligned.Users.Select(x => x.Resource.Id), Is.EqualTo(all.Users.Skip(1).Select(x => x.Resource.Id)));
        var beyondEnd = await service.Search(new SearchUsersQuery(admin.Id, 20, 2, UserRole.Teacher, TeacherApprovalStatus.Pending));
        Assert.That(beyondEnd.Users, Is.Empty);
    }

    [Test]
    public async Task AC2_MapperRoundTripsAllFieldsIncludingApprovedTeacherAndVersionMetadata()
    {
        var (_, admin) = await service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await service.ExecuteAndGet(new RegisterUserCommand("Teacher@Example.it", "password123", UserRole.Teacher));
        var (approvedContext, approved) = await service.ExecuteAndGet(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        var mapper = new UserDocumentMapper();
        var mapped = mapper.ToDocument(approved);
        Assert.That(mapped.Email, Is.EqualTo(approved.Email));
        Assert.That(mapped.NormalizedEmail, Is.EqualTo(approved.NormalizedEmail));
        Assert.That(mapped.PasswordHash, Is.EqualTo(approved.PasswordHash));
        Assert.That(mapped.Role, Is.EqualTo("Teacher"));
        Assert.That(mapped.TeacherApprovalStatus, Is.EqualTo("Approved"));
        Assert.That(mapped.SessionRevision, Is.EqualTo(approved.SessionRevision));
        var stored = await database.GetCollection<Document<UserDocument>>(MongoUsersRepository.CollectionName)
            .Find(x => x.Id == teacher.Id.Value.ToString()).SingleAsync();
        Assert.That(mapper.ToResource(stored), Is.EqualTo(approved));
        var restoredContext = stored.ToResourceContext();
        Assert.That(restoredContext.Version, Is.EqualTo(approvedContext.Version));
        Assert.That(restoredContext.CreatedAt, Is.EqualTo(approvedContext.CreatedAt));
    }

    [Test]
    public async Task AC2_ConcurrentRoleChangeAndApprovalCannotBothOverwriteTheSameVersion()
    {
        var (_, admin) = await service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await service.ExecuteAndGet(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        var racing = new UsersService(new SynchronizedReadRepository(repository, teacher.Id), new AspNetUsersPasswordHasher(), new NullUsersNotifier());
        async Task<string> Attempt(Func<Task> action)
        {
            try { await action(); return "success"; }
            catch (UsersVersionException) { return "stale"; }
        }
        var results = await Task.WhenAll(
            Attempt(() => racing.Execute(new ChangeUserRoleCommand(admin.Id, teacher.Id, context.Version, UserRole.Student))),
            Attempt(() => racing.Execute(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version))));
        Assert.That(results, Is.EquivalentTo(new[] { "success", "stale" }));
        var (current, actual) = await repository.Get(teacher.Id);
        Assert.That(current.Version, Is.EqualTo(context.Version + 1));
        Assert.That(actual.Role == UserRole.Student && actual.TeacherApprovalStatus is null ||
                    actual.Role == UserRole.Teacher && actual.TeacherApprovalStatus == TeacherApprovalStatus.Approved, Is.True);
    }

    private sealed class SynchronizedReadRepository(IUsersRepository inner, UserId target) : IUsersRepository
    {
        private int reads;
        private readonly TaskCompletionSource bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<(ResourceContext Context, User User)> Get(UserId id)
        {
            var result = await inner.Get(id);
            if (id == target)
            {
                if (Interlocked.Increment(ref reads) == 2) bothRead.TrySetResult();
                await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return result;
        }
        public Task<ResourceContext> Upsert(User user, int? version = null) => inner.Upsert(user, version);
        public Task<(ResourcesContext Context, ResourceEnvelope<User>[] Users)> Search(UsersQuery query) => inner.Search(query);
    }

    [Test]
    public async Task AC1_ReplayedRegistrationCannotOverwriteExistingIdentity()
    {
        var command = new RegisterUserCommand("replay@example.it", "password123", UserRole.Student);
        await service.Execute(command);
        var original = await service.Get(command.Id);
        Assert.ThrowsAsync<UsersVersionException>(() => service.Execute(command));
        var unchanged = await service.Get(command.Id);
        Assert.That(unchanged, Is.EqualTo(original));
    }

    [Test]
    public async Task AC1_ConcurrentRegistrationsHaveExactlyOneWinner()
    {
        async Task<bool> Register(string email)
        {
            try { await service.Execute(new RegisterUserCommand(email, "password123", UserRole.Student)); return true; }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
        }
        var results = await Task.WhenAll(Register("same@example.it"), Register("SAME@example.it"));
        Assert.That(results.Count(x => x), Is.EqualTo(1));
    }
}
