using System.ComponentModel.DataAnnotations;
using Icaro;

namespace CoursePilot.Host.Modules.Users;

public sealed record RegisterUserCommand(string Email, string Password, UserRole Role)
{
    public UserId Id { get; } = new(Guid.NewGuid());
}
public abstract record UsersQuery;
public sealed record UsersByEmailQuery(string NormalizedEmail) : UsersQuery;
public sealed record UsersListQuery(int Offset, int Limit, UserRole? Role, TeacherApprovalStatus? Approval) : UsersQuery;

public sealed record SearchUsersQuery(UserId ActorId, int Offset, int Limit, UserRole? Role, TeacherApprovalStatus? Approval);
public sealed record AuthenticateUserQuery(string Email, string Password);
public sealed record ChangeUserEmailCommand(UserId ActorId, UserId TargetId, int Version, string Email);
public sealed record ChangeUserRoleCommand(UserId ActorId, UserId TargetId, int Version, UserRole Role);
public sealed record ApproveTeacherCommand(UserId ActorId, UserId TargetId, int Version);
public sealed record BootstrapAdminCommand(string Email, string Password);
public sealed record AuthenticatedUser(UserId Id, int SessionRevision);

public interface IUsersService
{
    Task<(ResourceContext Context, User User)> Get(UserId id);
    Task<(ResourcesContext Context, ResourceEnvelope<User>[] Users)> Search(SearchUsersQuery query);
    Task Execute(RegisterUserCommand command);
    Task<AuthenticatedUser?> Search(AuthenticateUserQuery query);
    Task Execute(ChangeUserEmailCommand command);
    Task Execute(ChangeUserRoleCommand command);
    Task Execute(ApproveTeacherCommand command);
    Task Execute(BootstrapAdminCommand command);
}

public sealed class UsersService(IUsersRepository repository, IUsersPasswordHasher hasher, IUsersNotifier notifier) : IUsersService
{
    public async Task<(ResourceContext Context, User User)> Get(UserId id)
    {
        var result = await repository.Get(id);
        if (result.Context is null) throw new KeyNotFoundException();
        return result;
    }

    public async Task<(ResourcesContext Context, ResourceEnvelope<User>[] Users)> Search(SearchUsersQuery query)
    {
        await RequireAdmin(query.ActorId);
        if (query.Offset < 0 || query.Limit is < 1 or > 100 || query.Offset > int.MaxValue - query.Limit || (query.Role.HasValue && !Enum.IsDefined(query.Role.Value)) || (query.Approval.HasValue && !Enum.IsDefined(query.Approval.Value)))
            throw new ArgumentException("Invalid pagination or filter.");
        return await repository.Search(new UsersListQuery(query.Offset, query.Limit, query.Role, query.Approval));
    }

    public async Task Execute(RegisterUserCommand command)
    {
        if (command.Role is not (UserRole.Student or UserRole.Teacher)) throw new ArgumentException("Only student or teacher can register.");
        await Create(command.Id, command.Email, command.Password, command.Role);
    }

    public async Task<AuthenticatedUser?> Search(AuthenticateUserQuery query)
    {
        string email;
        try { email = NormalizeEmail(query.Email); }
        catch (ArgumentException) { return null; }
        var found = (await repository.Search(new UsersByEmailQuery(email))).Users.SingleOrDefault();
        if (found is null || string.IsNullOrEmpty(query.Password) || !hasher.Verify(found.Resource.PasswordHash, query.Password)) return null;
        return new(found.Resource.Id, found.Resource.SessionRevision);
    }

    public async Task Execute(ChangeUserEmailCommand command)
    {
        await RequireAdmin(command.ActorId);
        var (context, user) = await GetExpected(command.TargetId, command.Version);
        var normalized = NormalizeEmail(command.Email);
        string email = command.Email.Trim();
        if (user.Email == email) return;
        await Save(user with { Email = email, NormalizedEmail = normalized }, context.Version);
    }

    public async Task Execute(ChangeUserRoleCommand command)
    {
        await RequireAdmin(command.ActorId);
        if (!Enum.IsDefined(command.Role)) throw new ArgumentException("Invalid role.");
        var (context, user) = await GetExpected(command.TargetId, command.Version);
        if (user.Role == command.Role) return;
        await Save(user with { Role = command.Role, TeacherApprovalStatus = command.Role == UserRole.Teacher ? TeacherApprovalStatus.Pending : null, SessionRevision = checked(user.SessionRevision + 1) }, context.Version);
    }

    public async Task Execute(ApproveTeacherCommand command)
    {
        await RequireAdmin(command.ActorId);
        var (context, user) = await GetExpected(command.TargetId, command.Version);
        if (user.Role != UserRole.Teacher) throw new UsersConflictException("Target is not a teacher.");
        if (user.TeacherApprovalStatus == TeacherApprovalStatus.Approved) return;
        await Save(user with { TeacherApprovalStatus = TeacherApprovalStatus.Approved, SessionRevision = checked(user.SessionRevision + 1) }, context.Version);
    }

    public async Task Execute(BootstrapAdminCommand command)
    {
        var normalized = NormalizeEmail(command.Email);
        var existing = (await repository.Search(new UsersByEmailQuery(normalized))).Users.SingleOrDefault();
        if (existing is not null)
        {
            if (existing.Resource.Role != UserRole.Admin) throw new UsersConflictException("Email belongs to a non-admin account.");
            return;
        }
        await Create(new UserId(Guid.NewGuid()), command.Email, command.Password, UserRole.Admin);
    }

    private async Task Create(UserId id, string email, string password, UserRole role)
    {
        var normalized = NormalizeEmail(email);
        if (string.IsNullOrEmpty(password) || password.Length is < 8 or > 128) throw new ArgumentException("Password must contain 8–128 characters.");
        var user = new User(id, email.Trim(), normalized, hasher.Hash(password), role, role == UserRole.Teacher ? TeacherApprovalStatus.Pending : null, 0);
        // Version zero permits insertion but prevents a replayed command from updating an existing ID.
        await Save(user, 0);
    }

    private async Task Save(User user, int? version)
    {
        try
        {
            await repository.Upsert(user, version);
        }
        catch (InvalidOperationException) when (version.HasValue)
        {
            throw new UsersVersionException();
        }
        await notifier.UserChanged(user);
    }

    private async Task RequireAdmin(UserId actor)
    {
        var (_, user) = await Get(actor);
        if (user.Role != UserRole.Admin) throw new UnauthorizedAccessException();
    }

    private async Task<(ResourceContext Context, User User)> GetExpected(UserId id, int version)
    {
        var result = await Get(id);
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
