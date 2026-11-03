namespace CoursePilot.Host.Modules.Users;

public sealed class NullUsersNotifier : IUsersNotifier
{
    public Task UserChanged(User user) => Task.CompletedTask;
}
