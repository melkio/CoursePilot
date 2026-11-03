using CoursePilot.Host.Modules.Users;
using CoursePilot.Host.Modules.Users.Identity;
using Icaro;

namespace CoursePilot.Tests.Modules.Users;

public sealed class UsersServiceTests
{
    private MemoryRepository repository = null!; // Initialized before every test by NUnit.
    private UsersService service = null!; // Initialized before every test by NUnit.

    [SetUp]
    public void Setup()
    {
        repository = new MemoryRepository();
        service = new UsersService(repository, new AspNetUsersPasswordHasher(), new NullUsersNotifier());
    }

    [Test]
    public async Task AC1_StudentRegistrationRequiresEmailAndStoresHashedPassword()
    {
        Assert.ThrowsAsync<ArgumentException>(() => service.Execute(new RegisterUserCommand("", "password123", UserRole.Student)));
        Assert.ThrowsAsync<ArgumentException>(() => service.Execute(new RegisterUserCommand("bad", "password123", UserRole.Student)));
        var (_, user) = await service.ExecuteAndGet(new RegisterUserCommand(" Student@Example.it ", "password123", UserRole.Student));
        Assert.Multiple(() =>
        {
            Assert.That(user.Email, Is.EqualTo("Student@Example.it"));
            Assert.That(user.NormalizedEmail, Is.EqualTo("STUDENT@EXAMPLE.IT"));
            Assert.That(user.PasswordHash, Is.Not.EqualTo("password123"));
            Assert.That(user.TeacherApprovalStatus, Is.Null);
        });
        Assert.That((await service.Search(new AuthenticateUserQuery("student@example.IT", "password123")))?.Id, Is.EqualTo(user.Id));
        Assert.That(await service.Search(new AuthenticateUserQuery("student@example.it", "wrong")), Is.Null);
        Assert.That(await service.Search(new AuthenticateUserQuery("missing@example.it", "password123")), Is.Null);
    }

    [Test]
    public async Task AC2_OnlyAdminCanApprovePendingTeacherAndRoleChangesRequireReapproval()
    {
        var (_, admin) = await service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await service.ExecuteAndGet(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        Assert.That(teacher.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Pending));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Execute(new ApproveTeacherCommand(teacher.Id, teacher.Id, context.Version)));
        var (approvedContext, approved) = await service.ExecuteAndGet(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        Assert.That(approved.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Approved));
        Assert.That(approved.SessionRevision, Is.EqualTo(teacher.SessionRevision + 1));
        var (sameContext, same) = await service.ExecuteAndGet(new ApproveTeacherCommand(admin.Id, teacher.Id, approvedContext.Version));
        Assert.That(sameContext.Version, Is.EqualTo(approvedContext.Version));
        Assert.That(same.SessionRevision, Is.EqualTo(approved.SessionRevision));
        var (studentContext, student) = await service.ExecuteAndGet(new ChangeUserRoleCommand(admin.Id, teacher.Id, sameContext.Version, UserRole.Student));
        Assert.That(student.TeacherApprovalStatus, Is.Null);
        var (_, pendingAgain) = await service.ExecuteAndGet(new ChangeUserRoleCommand(admin.Id, teacher.Id, studentContext.Version, UserRole.Teacher));
        Assert.That(pendingAgain.TeacherApprovalStatus, Is.EqualTo(TeacherApprovalStatus.Pending));
    }

    [Test]
    public async Task AC2_AdminCannotSelfRegisterOrSpoofRoleAndStaleVersionIsRejected()
    {
        Assert.ThrowsAsync<ArgumentException>(() => service.Execute(new RegisterUserCommand("a@example.it", "password123", UserRole.Admin)));
        Assert.ThrowsAsync<ArgumentException>(() => service.Execute(new RegisterUserCommand("a@example.it", "password123", (UserRole)99)));
        var (_, admin) = await service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, student) = await service.ExecuteAndGet(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Execute(new ChangeUserRoleCommand(student.Id, student.Id, context.Version, UserRole.Admin)));
        await service.Execute(new ChangeUserRoleCommand(admin.Id, student.Id, context.Version, UserRole.Teacher));
        Assert.ThrowsAsync<UsersVersionException>(() => service.Execute(new ChangeUserEmailCommand(admin.Id, student.Id, context.Version, "new@example.it")));
    }

    [Test]
    public async Task AC2_BootstrapDoesNotOverwriteExistingAccount()
    {
        var (_, admin) = await service.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        await service.Execute(new BootstrapAdminCommand("ADMIN@example.it", "differentpassword"));
        var (_, again) = await service.Get(admin.Id);
        Assert.That(again, Is.EqualTo(admin));
        Assert.That(await service.Search(new AuthenticateUserQuery("admin@example.it", "password123")), Is.Not.Null);
        Assert.That(await service.Search(new AuthenticateUserQuery("admin@example.it", "differentpassword")), Is.Null);
        await service.Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        Assert.ThrowsAsync<UsersConflictException>(() => service.Execute(new BootstrapAdminCommand("STUDENT@example.it", "password123")));
    }

    private sealed class MemoryRepository : IUsersRepository
    {
        private readonly Dictionary<UserId, (ResourceContext Context, User User)> users = [];
        public Task<(ResourceContext Context, User User)> Get(UserId id) => Task.FromResult(users[id]);
        public Task<(ResourcesContext Context, ResourceEnvelope<User>[] Users)> Search(UsersQuery query)
        {
            var results = query switch
            {
                UsersByEmailQuery email => users.Values.Where(x => x.User.NormalizedEmail == email.NormalizedEmail),
                UsersListQuery list => users.Values.Where(x => (!list.Role.HasValue || x.User.Role == list.Role) && (!list.Approval.HasValue || x.User.TeacherApprovalStatus == list.Approval)),
                _ => throw new ArgumentException("Unknown query")
            };
            var matching = results.ToArray();
            var page = query is UsersListQuery listQuery ? matching.Skip(listQuery.Offset).Take(listQuery.Limit) : matching.Take(1);
            var items = page.Select(x => new ResourceEnvelope<User>(x.Context, x.User)).ToArray();
            return Task.FromResult((new ResourcesContext(matching.Length), items));
        }
        public Task<ResourceContext> Upsert(User user, int? version = null)
        {
            if (users.Values.Any(x => x.User.Id != user.Id && x.User.NormalizedEmail == user.NormalizedEmail)) throw new InvalidOperationException("Duplicate email.");
            var now = DateTimeOffset.UtcNow;
            if (users.TryGetValue(user.Id, out var existing) && existing.Context.Version != version) throw new UsersVersionException();
            var context = new ResourceContext((version ?? 0) + 1, users.TryGetValue(user.Id, out existing) ? existing.Context.CreatedAt : now, now);
            users[user.Id] = (context, user);
            return Task.FromResult(context);
        }
    }
}
