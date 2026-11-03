using System.Diagnostics;
using CoursePilot.Host.Modules.Users;
using CoursePilot.Host.Modules.Users.Identity;
using CoursePilot.Host.Modules.Users.Mongo;
using MongoDB.Driver;

namespace CoursePilot.Tests.Modules.Users.Transports.Cli;

public sealed class BootstrapAdminTests
{
    [Test]
    public async Task AC2_OperatorCliIsIdempotentAndRejectsNonAdminCollision()
    {
        var uri = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000";
        var databaseName = "coursepilot_bootstrap_" + Guid.NewGuid().ToString("N");
        using var mongo = new MongoClient(uri);
        var database = mongo.GetDatabase(databaseName);
        try
        {
            await new UsersMongoInitializer(database).Initialize();
            var users = new UsersService(new UsersRepositoryErrorTranslation(new MongoUsersRepository(database, new UserDocumentMapper())), new AspNetUsersPasswordHasher(), new NullUsersNotifier());
            await users.Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
            Assert.That(await RunCli(uri, databaseName, "admin@example.it", "password123"), Is.EqualTo(0));
            Assert.That(await RunCli(uri, databaseName, "ADMIN@example.it", "differentpassword"), Is.EqualTo(0));
            Assert.That(await users.Search(new AuthenticateUserQuery("admin@example.it", "password123")), Is.Not.Null);
            Assert.That(await users.Search(new AuthenticateUserQuery("admin@example.it", "differentpassword")), Is.Null);
            Assert.That(await RunCli(uri, databaseName, "STUDENT@example.it", "password123"), Is.Not.EqualTo(0));
            var student = await users.Search(new AuthenticateUserQuery("student@example.it", "password123"));
            Assert.That(student, Is.Not.Null);
            Assert.That((await users.Get(student!.Id)).User.Role, Is.EqualTo(UserRole.Student)); // Checked non-null immediately above.
        }
        finally { await mongo.DropDatabaseAsync(databaseName); }
    }

    private static async Task<int> RunCli(string uri, string database, string email, string password)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("bootstrap-admin");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["MongoDB__ConnectionString"] = new MongoUrlBuilder(uri) { DatabaseName = database }.ToString();
        start.Environment["BootstrapAdmin__Email"] = email;
        start.Environment["BootstrapAdmin__Password"] = password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start bootstrap CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { process.Kill(true); throw; }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }
}
