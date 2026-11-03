using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoursePilot.Host.Modules.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace CoursePilot.Tests.Modules.Users.Transports.Http;

// This controller is registered only with the test host; it is not part of the production application.
[ApiController]
[Route("test-policy")]
public sealed class PolicyProbeController : ControllerBase
{
    [Authorize(Policy = "Student"), HttpGet("student")]
    public IActionResult Student() => Ok();
    [Authorize(Policy = "ApprovedTeacher"), HttpGet("teacher")]
    public IActionResult Teacher() => Ok();
    [Authorize(Policy = "Admin"), HttpGet("admin")]
    public IActionResult Admin() => Ok();
}

public sealed class UsersAuthorizationTests
{
    private MongoClient _mongo = null!; // Assigned by NUnit setup.
    private WebApplicationFactory<Program> _factory = null!; // Assigned by NUnit setup.
    private string _db = string.Empty;
    private string _uri = string.Empty;
    private string _keys = string.Empty;

    [SetUp]
    public void Setup()
    {
        _uri = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000";
        _db = "coursepilot_policy_" + Guid.NewGuid().ToString("N");
        _keys = Path.Combine(Path.GetTempPath(), "coursepilot-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keys);
        _mongo = new MongoClient(_uri);
        _factory = CreateFactory();
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Mongo:ConnectionString", _uri);
        builder.UseSetting("Mongo:Database", _db);
        builder.UseSetting("DataProtection:KeysPath", _keys);
        builder.ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(PolicyProbeController).Assembly));
    });

    [TearDown]
    public async Task Cleanup()
    {
        try
        {
            await _factory.DisposeAsync();
            await _mongo.DropDatabaseAsync(_db);
            Directory.Delete(_keys, true);
        }
        finally { _mongo.Dispose(); }
    }

    [Test]
    public async Task AC2_PoliciesEnforceExclusiveRolesOnActualHttpEndpoints()
    {
        using var anonymous = Client(_factory);
        using var admin = Client(_factory);
        using var student = Client(_factory);
        using var teacher = Client(_factory);
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUsersService>();
        var (_, administrator) = await users.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        await users.Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        var (version, pending) = await users.Execute(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        Assert.That((await anonymous.GetAsync("/test-policy/student")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await Login(admin, "admin@example.it");
        await Login(student, "student@example.it");
        await Login(teacher, "teacher@example.it");
        await Check(admin, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.OK);
        await Check(student, HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden);
        await Check(teacher, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden);
        await users.Execute(new ApproveTeacherCommand(administrator.Id, pending.Id, version.Version));
        Assert.That((await teacher.GetAsync("/test-policy/teacher")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await Login(teacher, "teacher@example.it");
        await Check(teacher, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AC2_UnavailablePersistenceRejectsExistingCookie()
    {
        using var client = Client(_factory);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUsersService>().Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        await Login(client, "student@example.it");
        Assert.That((await client.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        // Dispose only the host's client; the independently owned cleanup client remains usable.
        ((MongoClient)_factory.Services.GetRequiredService<IMongoClient>()).Dispose();
        Assert.That((await client.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AC2_ProductionCookieIsSecureAndSurvivesReplicaAndRestart()
    {
        using var client = Client(_factory);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUsersService>().Execute(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        var cookie = await Login(client, "student@example.it");
        Assert.That(cookie, Does.Contain("secure").IgnoreCase);
        Assert.That(cookie, Does.Contain("httponly").IgnoreCase);
        Assert.That(cookie, Does.Contain("samesite=lax").IgnoreCase);
        var pair = cookie.Split(';')[0];
        await using var replica = CreateFactory();
        using var replicaClient = replica.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        replicaClient.DefaultRequestHeaders.Add("Cookie", pair);
        Assert.That((await replicaClient.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await replica.DisposeAsync();
        await using var restarted = CreateFactory();
        using var restartedClient = restarted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        restartedClient.DefaultRequestHeaders.Add("Cookie", pair);
        Assert.That((await restartedClient.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<string> Login(HttpClient client, string email)
    {
        var token = await client.GetAsync("/api/users/antiforgery");
        token.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("token").GetString());
        var result = await client.PostAsJsonAsync("/api/users/login", new { email, password = "password123" });
        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK), await result.Content.ReadAsStringAsync());
        return result.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
    }

    private static async Task Check(HttpClient client, HttpStatusCode student, HttpStatusCode teacher, HttpStatusCode admin)
    {
        Assert.That((await client.GetAsync("/test-policy/student")).StatusCode, Is.EqualTo(student));
        Assert.That((await client.GetAsync("/test-policy/teacher")).StatusCode, Is.EqualTo(teacher));
        Assert.That((await client.GetAsync("/test-policy/admin")).StatusCode, Is.EqualTo(admin));
    }
}
