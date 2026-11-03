using CoursePilot.Host.Modules.Users;
using CoursePilot.Host.Modules.Users.Identity;
using Icaro;

namespace CoursePilot.Tests.Modules.Users;

public sealed class UsersServiceTests
{
    private MemoryRepository _repository = null!; // Initialized before every test by NUnit.
    private UsersService _service = null!; // Initialized before every test by NUnit.

    [SetUp]
    public void Setup()
    {
        _repository = new MemoryRepository();
        _service = new UsersService(_repository, new AspNetUsersPasswordHasher(), new NullUsersNotifier());
    }

    [Test]
    public async Task AC1_StudentRegistrationRequiresEmailAndStoresHashedPassword()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _service.Execute(new RegisterUserCommand("", "password123", UserRole.Student)));
        Assert.ThrowsAsync<ArgumentException>(() => _service.Execute(new RegisterUserCommand("bad", "password123", UserRole.Student)));
        var (_, user) = await _service.Execute(new RegisterUserCommand(" Student@Example.it ", "password123", UserRole.Student));
        Assert.Multiple(() =>
        {
            Assert.That(user.Email, Is.EqualTo("Student@Example.it"));
            Assert.That(user.NormalizedEmail, Is.EqualTo("STUDENT@EXAMPLE.IT"));
            Assert.That(user.PasswordHash, Is.Not.EqualTo("password123"));
            Assert.That(user.TeacherApprovalStatus, Is.Null);
        });
        Assert.That((await _service.Execute(new AuthenticateUserCommand("student@example.IT", "password123")))?.Id, Is.EqualTo(user.Id));
        Assert.That(await _service.Execute(new AuthenticateUserCommand("student@example.it", "wrong")), Is.Null);
        Assert.That(await _service.Execute(new AuthenticateUserCommand("missing@example.it", "password123")), Is.Null);
    }

    [Test]
    public async Task AC2_OnlyAdminCanApprovePendingTeacherAndRoleChangesRequireReapproval()
    {
        var (_, admin) = await _service.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await _service.Execute(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        Assert.That(teacher.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Pending));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.Execute(new ApproveTeacherCommand(teacher.Id, teacher.Id, context.Version)));
        var (approvedContext, approved) = await _service.Execute(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        Assert.That(approved.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Approved));
        Assert.That(approved.SessionRevision, Is.EqualTo(teacher.SessionRevision + 1));
        var (sameContext, same) = await _service.Execute(new ApproveTeacherCommand(admin.Id, teacher.Id, approvedContext.Version));
        Assert.That(sameContext.Version, Is.EqualTo(approvedContext.Version));
        Assert.That(same.SessionRevision, Is.EqualTo(approved.SessionRevision));
        var (studentContext, student) = await _service.Execute(new ChangeUserRoleCommand(admin.Id, teacher.Id, sameContext.Version, UserRole.Student));
        Assert.That(student.TeacherApprovalStatus, Is.Null);
        var (_, pendingAgain) = await _service.Execute(new ChangeUserRoleCommand(admin.Id, teacher.Id, studentContext.Version, UserRole.Teacher));
        Assert.That(pendingAgain.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Pending));
    }

    [Test]
    public async Task AC2_AdminCannotSelfRegisterOrSpoofRoleAndStaleVersionIsRejected()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _service.Execute(new RegisterUserCommand("a@example.it", "password123", UserRole.Admin)));
        Assert.ThrowsAsync<ArgumentException>(() => _service.Execute(new RegisterUserCommand("a@example.it", "password123", (UserRole)99)));
        var (_, admin) = await _service.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, student) = await _service.Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.Execute(new ChangeUserRoleCommand(student.Id, student.Id, context.Version, UserRole.Admin)));
        await _service.Execute(new ChangeUserRoleCommand(admin.Id, student.Id, context.Version, UserRole.Teacher));
        Assert.ThrowsAsync<UsersVersionException>(() => _service.Execute(new ChangeUserEmailCommand(admin.Id, student.Id, context.Version, "new@example.it")));
    }

    [Test]
    public async Task AC2_BootstrapDoesNotOverwriteExistingAccount()
    {
        var (_, admin) = await _service.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (_, again) = await _service.Execute(new BootstrapAdminCommand("ADMIN@example.it", "differentpassword"));
        Assert.That(again, Is.EqualTo(admin));
        Assert.That(await _service.Execute(new AuthenticateUserCommand("admin@example.it", "password123")), Is.Not.Null);
        Assert.That(await _service.Execute(new AuthenticateUserCommand("admin@example.it", "differentpassword")), Is.Null);
        await _service.Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        Assert.ThrowsAsync<UsersConflictException>(() => _service.Execute(new BootstrapAdminCommand("STUDENT@example.it", "password123")));
    }

    private sealed class MemoryRepository : IUsersRepository
    {
        private readonly Dictionary<UserId, (ResourceContext Context, User User)> _users = [];
        public Task<(ResourceContext Context, User User)> Get(UserId id) => Task.FromResult(_users[id]);
        public Task<(ResourceContext Context, User User)?> FindByEmail(string email) => Task.FromResult<(ResourceContext, User)?>(_users.Values.Where(x => x.User.NormalizedEmail == email).Select(x => ((ResourceContext, User)?)x).SingleOrDefault());
        public Task<IReadOnlyList<(ResourceContext Context, User User)>> List(int offset, int limit, UserRole? role, TeacherApprovalStatus? approval) => Task.FromResult<IReadOnlyList<(ResourceContext, User)>>(_users.Values.Where(x => (!role.HasValue || x.User.Role == role) && (!approval.HasValue || x.User.TeacherApprovalStatus == approval)).Skip(offset).Take(limit).ToArray());
        public Task<ResourceContext> Upsert(User user, int? version = null)
        {
            if (_users.Values.Any(x => x.User.Id != user.Id && x.User.NormalizedEmail == user.NormalizedEmail)) throw new InvalidOperationException("Duplicate email.");
            var now = DateTimeOffset.UtcNow;
            if (_users.TryGetValue(user.Id, out var existing) && existing.Context.Version != version) throw new UsersVersionException();
            var context = new ResourceContext((version ?? 0) + 1, _users.TryGetValue(user.Id, out existing) ? existing.Context.CreatedAt : now, now);
            _users[user.Id] = (context, user);
            return Task.FromResult(context);
        }
    }
}
