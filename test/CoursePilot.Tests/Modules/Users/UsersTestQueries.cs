using CoursePilot.Host.Modules.Users;
using Icaro;

namespace CoursePilot.Tests.Modules.Users;

// Tests observe command results through the same query boundary used by transports.
internal static class UsersTestQueries
{
    public static async Task<(ResourceContext Context, User User)> ExecuteAndGet(this IUsersService service, RegisterUserCommand command)
    {
        await service.Execute(command);
        return await service.Get(command.Id);
    }

    public static async Task<(ResourceContext Context, User User)> ExecuteAndGet(this IUsersService service, BootstrapAdminCommand command)
    {
        await service.Execute(command);
        var identity = await service.Search(new AuthenticateUserQuery(command.Email, command.Password));
        if (identity is null) throw new InvalidOperationException("Bootstrap account could not authenticate.");
        return await service.Get(identity.Id);
    }

    public static async Task<(ResourceContext Context, User User)> ExecuteAndGet(this IUsersService service, ApproveTeacherCommand command)
    {
        await service.Execute(command);
        return await service.Get(command.TargetId);
    }

    public static async Task<(ResourceContext Context, User User)> ExecuteAndGet(this IUsersService service, ChangeUserRoleCommand command)
    {
        await service.Execute(command);
        return await service.Get(command.TargetId);
    }
}
