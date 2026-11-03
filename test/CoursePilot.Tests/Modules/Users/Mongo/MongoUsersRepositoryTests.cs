using CoursePilot.Host.Modules.Users;
using CoursePilot.Host.Modules.Users.Identity;
using CoursePilot.Host.Modules.Users.Mongo;
using Icaro;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CoursePilot.Tests.Modules.Users.Mongo;

public sealed class MongoUsersRepositoryTests
{
    private MongoClient _client = null!; // Initialized by NUnit before each test.
    private IMongoDatabase _database = null!; // Initialized by NUnit before each test.
    private IUsersRepository _repository = null!; // Initialized by NUnit before each test.
    private UsersService _service = null!; // Initialized by NUnit before each test.
    private string _name = string.Empty;

    [SetUp]
    public async Task Setup()
    {
        _client = new MongoClient(Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000");
        _name = "coursepilot_test_" + Guid.NewGuid().ToString("N");
        _database = _client.GetDatabase(_name);
        await new UsersMongoInitializer(_database).Initialize();
        _repository = new UsersRepositoryErrorTranslation(new MongoUsersRepository(_database, new UserDocumentMapper()));
        _service = new UsersService(_repository, new AspNetUsersPasswordHasher(), new NullUsersNotifier());
    }

    [TearDown]
    public async Task Cleanup()
    {
        try { if (!string.IsNullOrEmpty(_name)) await _client.DropDatabaseAsync(_name); }
        finally { _client?.Dispose(); }
    }

    [Test]
    public async Task AC1_UniqueNormalizedEmailAndDocumentRoundTripSurviveNewRepository()
    {
        var (_, user) = await _service.ExecuteAndGet(new RegisterUserCommand("Student@Example.it", "password123", UserRole.Student));
        Assert.ThrowsAsync<UsersDuplicateEmailException>(() => _service.Execute(new RegisterUserCommand("student@example.IT", "password456", UserRole.Student)));
        var fresh = new MongoUsersRepository(_database, new UserDocumentMapper());
        var (context, loaded) = await fresh.Get(user.Id);
        Assert.That(loaded, Is.EqualTo(user));
        Assert.That(context.Version, Is.EqualTo(1));
        var raw = await _database.GetCollection<BsonDocument>(MongoUsersRepository.CollectionName).Find(new BsonDocument()).SingleAsync();
        Assert.That(raw["Value"]["NormalizedEmail"].AsString, Is.EqualTo("STUDENT@EXAMPLE.IT"));
        Assert.That(raw["Value"]["Role"].AsString, Is.EqualTo("Student"));
    }

    [Test]
    public async Task AC2_MongoVersionRejectsLostUpdateAndFiltersOnServer()
    {
        var (_, admin) = await _service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await _service.ExecuteAndGet(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        var (changedContext, _) = await _service.ExecuteAndGet(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        Assert.ThrowsAsync<UsersVersionException>(() => _repository.Upsert(teacher with { Role = UserRole.Student, TeacherApprovalStatus = null }, context.Version));
        var (current, loaded) = await _repository.Get(teacher.Id);
        Assert.That(current.Version, Is.EqualTo(changedContext.Version));
        Assert.That(loaded.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Approved));
        var teachers = await _repository.Search(new UsersListQuery(0, 1, UserRole.Teacher, TeacherApprovalStatus.Approved));
        Assert.That(teachers.Select(x => x.User.Id), Is.EquivalentTo(new[] { teacher.Id }));
    }

    [Test]
    public async Task AC2_FilteredListIsPagedInMongo()
    {
        var (_, admin) = await _service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        for (var i = 0; i < 3; i++)
            await _service.Execute(new RegisterUserCommand($"teacher{i}@example.it", "password123", UserRole.Teacher));
        await _service.Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        var first = await _service.List(admin.Id, 0, 2, UserRole.Teacher, TeacherApprovalStatus.Pending);
        var second = await _service.List(admin.Id, 2, 2, UserRole.Teacher, TeacherApprovalStatus.Pending);
        Assert.That(first.Count, Is.EqualTo(2));
        Assert.That(second.Count, Is.EqualTo(1));
        Assert.That(first.Select(x => x.User.Id).Intersect(second.Select(x => x.User.Id)), Is.Empty);
        Assert.That(first.Concat(second).All(x => x.User.Role == UserRole.Teacher && x.User.TeacherApprovalStatus == TeacherApprovalStatus.Pending), Is.True);
        var all = await _service.List(admin.Id, 0, 3, UserRole.Teacher, TeacherApprovalStatus.Pending);
        var unaligned = await _service.List(admin.Id, 1, 2, UserRole.Teacher, TeacherApprovalStatus.Pending);
        Assert.That(unaligned.Select(x => x.User.Id), Is.EqualTo(all.Skip(1).Select(x => x.User.Id)));
        var beyondEnd = await _service.List(admin.Id, 20, 2, UserRole.Teacher, TeacherApprovalStatus.Pending);
        Assert.That(beyondEnd, Is.Empty);
    }

    [Test]
    public async Task AC2_MapperRoundTripsAllFieldsIncludingApprovedTeacherAndVersionMetadata()
    {
        var (_, admin) = await _service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await _service.ExecuteAndGet(new RegisterUserCommand("Teacher@Example.it", "password123", UserRole.Teacher));
        var (approvedContext, approved) = await _service.ExecuteAndGet(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        var mapper = new UserDocumentMapper();
        var mapped = mapper.ToDocument(approved);
        Assert.That(mapped.Email, Is.EqualTo(approved.Email));
        Assert.That(mapped.NormalizedEmail, Is.EqualTo(approved.NormalizedEmail));
        Assert.That(mapped.PasswordHash, Is.EqualTo(approved.PasswordHash));
        Assert.That(mapped.Role, Is.EqualTo("Teacher"));
        Assert.That(mapped.TeacherApprovalStatus, Is.EqualTo("Approved"));
        Assert.That(mapped.SessionRevision, Is.EqualTo(approved.SessionRevision));
        var stored = await _database.GetCollection<Document<UserDocument>>(MongoUsersRepository.CollectionName)
            .Find(x => x.Id == teacher.Id.Value.ToString()).SingleAsync();
        Assert.That(mapper.ToResource(stored), Is.EqualTo(approved));
        var restoredContext = stored.ToResourceContext();
        Assert.That(restoredContext.Version, Is.EqualTo(approvedContext.Version));
        Assert.That(restoredContext.CreatedAt, Is.EqualTo(approvedContext.CreatedAt));
    }

    [Test]
    public async Task AC2_ConcurrentRoleChangeAndApprovalCannotBothOverwriteTheSameVersion()
    {
        var (_, admin) = await _service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await _service.ExecuteAndGet(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        var racing = new UsersService(new SynchronizedReadRepository(_repository, teacher.Id), new AspNetUsersPasswordHasher(), new NullUsersNotifier());
        async Task<string> Attempt(Func<Task> action)
        {
            try { await action(); return "success"; }
            catch (UsersVersionException) { return "stale"; }
        }
        var results = await Task.WhenAll(
            Attempt(() => racing.Execute(new ChangeUserRoleCommand(admin.Id, teacher.Id, context.Version, UserRole.Student))),
            Attempt(() => racing.Execute(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version))));
        Assert.That(results, Is.EquivalentTo(new[] { "success", "stale" }));
        var (current, actual) = await _repository.Get(teacher.Id);
        Assert.That(current.Version, Is.EqualTo(context.Version + 1));
        Assert.That(actual.Role == UserRole.Student && actual.TeacherApprovalStatus is null ||
                    actual.Role == UserRole.Teacher && actual.TeacherApprovalStatus == TeacherApprovalStatus.Approved, Is.True);
    }

    private sealed class SynchronizedReadRepository(IUsersRepository inner, UserId target) : IUsersRepository
    {
        private int _reads;
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<(ResourceContext Context, User User)> Get(UserId id)
        {
            var result = await inner.Get(id);
            if (id == target)
            {
                if (Interlocked.Increment(ref _reads) == 2) _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return result;
        }
        public Task<ResourceContext> Upsert(User user, int? version = null) => inner.Upsert(user, version);
        public Task<IReadOnlyList<(ResourceContext Context, User User)>> Search(UsersQuery query) => inner.Search(query);
    }

    [Test]
    public async Task AC1_ReplayedRegistrationCannotOverwriteExistingIdentity()
    {
        var command = new RegisterUserCommand("replay@example.it", "password123", UserRole.Student);
        await _service.Execute(command);
        var original = await _service.Get(command.Id);
        Assert.ThrowsAsync<UsersVersionException>(() => _service.Execute(command));
        var unchanged = await _service.Get(command.Id);
        Assert.That(unchanged, Is.EqualTo(original));
    }

    [Test]
    public async Task AC1_ConcurrentRegistrationsHaveExactlyOneWinner()
    {
        async Task<bool> Register(string email)
        {
            try { await _service.Execute(new RegisterUserCommand(email, "password123", UserRole.Student)); return true; }
            catch (UsersDuplicateEmailException) { return false; }
        }
        var results = await Task.WhenAll(Register("same@example.it"), Register("SAME@example.it"));
        Assert.That(results.Count(x => x), Is.EqualTo(1));
    }
}
