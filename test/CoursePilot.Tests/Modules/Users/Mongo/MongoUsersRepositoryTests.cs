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
    private MongoUsersRepository _repository = null!; // Initialized by NUnit before each test.
    private UsersService _service = null!; // Initialized by NUnit before each test.
    private string _name = string.Empty;

    [SetUp]
    public async Task Setup()
    {
        _client = new MongoClient(Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000");
        _name = "coursepilot_test_" + Guid.NewGuid().ToString("N");
        _database = _client.GetDatabase(_name);
        await new UsersMongoInitializer(_database).Initialize();
        _repository = new MongoUsersRepository(_database, new UserDocumentMapper());
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
        var (_, user) = await _service.Execute(new RegisterUserCommand("Student@Example.it", "password123", UserRole.Student));
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
        var (_, admin) = await _service.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await _service.Execute(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        var (changedContext, _) = await _service.Execute(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        Assert.ThrowsAsync<UsersVersionException>(() => _repository.Upsert(teacher with { Role = UserRole.Student, TeacherApprovalStatus = null }, context.Version));
        var (current, loaded) = await _repository.Get(teacher.Id);
        Assert.That(current.Version, Is.EqualTo(changedContext.Version));
        Assert.That(loaded.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Approved));
        var teachers = await _repository.List(0, 1, UserRole.Teacher, TeacherApprovalStatus.Approved);
        Assert.That(teachers.Select(x => x.User.Id), Is.EquivalentTo(new[] { teacher.Id }));
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
