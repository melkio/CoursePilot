using System.ComponentModel.DataAnnotations;
using Icaro;

namespace CoursePilot.Host.Modules.Users;

public sealed record RegisterUserCommand(string Email, string Password, UserRole Role);
public sealed record AuthenticateUserCommand(string Email, string Password);
public sealed record ChangeUserEmailCommand(UserId ActorId, UserId TargetId, int Version, string Email);
public sealed record ChangeUserRoleCommand(UserId ActorId, UserId TargetId, int Version, UserRole Role);
public sealed record ApproveTeacherCommand(UserId ActorId, UserId TargetId, int Version);
public sealed record BootstrapAdminCommand(string Email, string Password);
public sealed record AuthenticatedUser(UserId Id, int SessionRevision);

public interface IUsersService
{
    Task<(ResourceContext Context, User User)> Get(UserId id);
    Task<IReadOnlyList<(ResourceContext Context, User User)>> List(UserId actorId, int offset, int limit, UserRole? role, TeacherApprovalStatus? approval);
    Task<(ResourceContext Context, User User)> Execute(RegisterUserCommand command);
    Task<AuthenticatedUser?> Execute(AuthenticateUserCommand command);
    Task<(ResourceContext Context, User User)> Execute(ChangeUserEmailCommand command);
    Task<(ResourceContext Context, User User)> Execute(ChangeUserRoleCommand command);
    Task<(ResourceContext Context, User User)> Execute(ApproveTeacherCommand command);
    Task<(ResourceContext Context, User User)> Execute(BootstrapAdminCommand command);
}

public sealed class UsersService(IUsersRepository repository, IUsersPasswordHasher hasher, IUsersNotifier notifier) : IUsersService
{
    public Task<(ResourceContext Context, User User)> Get(UserId id) => repository.Get(id);

    public async Task<IReadOnlyList<(ResourceContext Context, User User)>> List(UserId actorId, int offset, int limit, UserRole? role, TeacherApprovalStatus? approval)
    {
        await RequireAdmin(actorId);
        if (offset < 0 || limit is < 1 or > 100 || (role.HasValue && !Enum.IsDefined(role.Value)) || (approval.HasValue && !Enum.IsDefined(approval.Value)))
            throw new ArgumentException("Invalid pagination or filter.");
        return await repository.List(offset, limit, role, approval);
    }

    public async Task<(ResourceContext Context, User User)> Execute(RegisterUserCommand command)
    {
        if (command.Role is not (UserRole.Student or UserRole.Teacher)) throw new ArgumentException("Only student or teacher can register.");
        return await Create(command.Email, command.Password, command.Role);
    }

    public async Task<AuthenticatedUser?> Execute(AuthenticateUserCommand command)
    {
        string email;
        try { email = NormalizeEmail(command.Email); }
        catch (ArgumentException) { return null; }
        var found = await repository.FindByEmail(email);
        if (found is null || string.IsNullOrEmpty(command.Password) || !hasher.Verify(found.Value.User.PasswordHash, command.Password)) return null;
        return new(found.Value.User.Id, found.Value.User.SessionRevision);
    }

    public async Task<(ResourceContext Context, User User)> Execute(ChangeUserEmailCommand command)
    {
        await RequireAdmin(command.ActorId);
        var (context, user) = await GetExpected(command.TargetId, command.Version);
        var normalized = NormalizeEmail(command.Email);
        string email = command.Email.Trim();
        if (user.Email == email) return (context, user);
        return await Save(user with { Email = email, NormalizedEmail = normalized }, context.Version);
    }

    public async Task<(ResourceContext Context, User User)> Execute(ChangeUserRoleCommand command)
    {
        await RequireAdmin(command.ActorId);
        if (!Enum.IsDefined(command.Role)) throw new ArgumentException("Invalid role.");
        var (context, user) = await GetExpected(command.TargetId, command.Version);
        if (user.Role == command.Role) return (context, user);
        return await Save(user with { Role = command.Role, TeacherApprovalStatus = command.Role == UserRole.Teacher ? TeacherApprovalStatus.Pending : null, SessionRevision = checked(user.SessionRevision + 1) }, context.Version);
    }

    public async Task<(ResourceContext Context, User User)> Execute(ApproveTeacherCommand command)
    {
        await RequireAdmin(command.ActorId);
        var (context, user) = await GetExpected(command.TargetId, command.Version);
        if (user.Role != UserRole.Teacher) throw new UsersConflictException("Target is not a teacher.");
        if (user.TeacherApprovalStatus == TeacherApprovalStatus.Approved) return (context, user);
        return await Save(user with { TeacherApprovalStatus = TeacherApprovalStatus.Approved, SessionRevision = checked(user.SessionRevision + 1) }, context.Version);
    }

    public async Task<(ResourceContext Context, User User)> Execute(BootstrapAdminCommand command)
    {
        var normalized = NormalizeEmail(command.Email);
        var existing = await repository.FindByEmail(normalized);
        if (existing is not null)
        {
            if (existing.Value.User.Role != UserRole.Admin) throw new UsersConflictException("Email belongs to a non-admin account.");
            return existing.Value;
        }
        return await Create(command.Email, command.Password, UserRole.Admin);
    }

    private async Task<(ResourceContext Context, User User)> Create(string email, string password, UserRole role)
    {
        var normalized = NormalizeEmail(email);
        if (string.IsNullOrEmpty(password) || password.Length is < 8 or > 128) throw new ArgumentException("Password must contain 8–128 characters.");
        var user = new User(new UserId(Guid.NewGuid()), email.Trim(), normalized, hasher.Hash(password), role, role == UserRole.Teacher ? TeacherApprovalStatus.Pending : null, 0);
        return await Save(user, null);
    }

    private async Task<(ResourceContext Context, User User)> Save(User user, int? version)
    {
        var context = await repository.Upsert(user, version);
        await notifier.UserChanged(user);
        return (context, user);
    }

    private async Task RequireAdmin(UserId actor)
    {
        var (_, user) = await repository.Get(actor);
        if (user.Role != UserRole.Admin) throw new UnauthorizedAccessException();
    }

    private async Task<(ResourceContext Context, User User)> GetExpected(UserId id, int version)
    {
        var result = await repository.Get(id);
        if (result.Context.Version != version) throw new UsersVersionException();
        return result;
    }

    public static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Email is required.");
        email = email.Trim();
        if (email.Length > 254 || !new EmailAddressAttribute().IsValid(email)) throw new ArgumentException("Invalid email.");
        return email.ToUpperInvariant();
    }
}

public sealed class UsersVersionException : Exception;
public sealed class UsersDuplicateEmailException : Exception;
public sealed class UsersConflictException(string message) : InvalidOperationException(message);
