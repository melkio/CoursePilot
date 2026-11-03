using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoursePilot.Host.Modules.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace CoursePilot.Tests.Modules.Users.Transports.Http;

public sealed class UsersHttpTests
{
    private WebApplicationFactory<Program> _factory = null!; // Initialized by NUnit before each test.
    private MongoClient _mongo = null!; // Initialized by NUnit before each test.
    private string _db = string.Empty;
    private string _uri = string.Empty;

    [SetUp]
    public void Setup()
    {
        _uri = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000";
        _db = "coursepilot_http_" + Guid.NewGuid().ToString("N");
        _mongo = new MongoClient(_uri);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Mongo:ConnectionString", _uri);
            builder.UseSetting("Mongo:Database", _db);
        });
    }

    [TearDown]
    public async Task Cleanup()
    {
        try { await _factory.DisposeAsync(); await _mongo.DropDatabaseAsync(_db); }
        finally { _mongo.Dispose(); }
    }

    [Test]
    public async Task AC2_TeacherApprovalInvalidatesCookieAndAdminOperationsRequireCsrf()
    {
        using var teacher = _factory.CreateClient();
        using var admin = _factory.CreateClient();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUsersService>().Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var noCsrf = await teacher.PostAsJsonAsync("/api/users/register", new { email = "teacher@example.it", password = "password123", role = "Teacher" });
        Assert.That(noCsrf.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await Token(teacher);
        var register = await teacher.PostAsJsonAsync("/api/users/register", new { email = "teacher@example.it", password = "password123", role = "Teacher" });
        Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        using var registration = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        var teacherId = registration.RootElement.GetProperty("id").GetGuid();
        Assert.That(registration.RootElement.GetProperty("teacherApprovalStatus").GetString(), Is.EqualTo("Pending"));
        Assert.That(registration.RootElement.TryGetProperty("passwordHash", out _), Is.False);
        var login = await teacher.PostAsJsonAsync("/api/users/login", new { email = "teacher@example.it", password = "password123" });
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await teacher.GetAsync("/api/admin/users")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        await Token(admin);
        Assert.That((await admin.PostAsJsonAsync("/api/users/login", new { email = "admin@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(admin);
        var target = await admin.GetAsync($"/api/admin/users/{teacherId}");
        var etag = target.Headers.ETag?.ToString();
        Assert.That(etag, Is.Not.Null);
        using var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{teacherId}/approve-teacher");
        approve.Headers.TryAddWithoutValidation("If-Match", etag);
        var approval = await admin.SendAsync(approve);
        Assert.That(approval.StatusCode, Is.EqualTo(HttpStatusCode.OK), await approval.Content.ReadAsStringAsync());
        Assert.That((await teacher.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await Token(teacher);
        Assert.That((await teacher.PostAsJsonAsync("/api/users/login", new { email = "teacher@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var me = JsonDocument.Parse(await (await teacher.GetAsync("/api/users/me")).Content.ReadAsStringAsync());
        Assert.That(me.RootElement.GetProperty("teacherApprovalStatus").GetString(), Is.EqualTo("Approved"));
    }

    [Test]
    public async Task AC1_InvalidInputsAndUnknownPrivilegedFieldsAreRejected()
    {
        using var client = _factory.CreateClient();
        await Token(client);
        var invalid = await client.PostAsJsonAsync("/api/users/register", new { email = "", password = "password123", role = "Student" });
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var spoof = await client.PostAsJsonAsync("/api/users/register", new { email = "person@example.it", password = "password123", role = "Student", teacherApprovalStatus = "Approved" });
        Assert.That(spoof.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var escalation = await client.PostAsJsonAsync("/api/users/register", new { email = "person@example.it", password = "password123", role = "Admin" });
        Assert.That(escalation.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AC1_ChangeEmailKeepsIdAndStaleOrMissingIfMatchFails()
    {
        using var client = _factory.CreateClient();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUsersService>().Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        Assert.That((await client.GetAsync("/api/admin/users")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await Token(client);
        var created = await client.PostAsJsonAsync("/api/users/register", new { email = "student@example.it", password = "password123", role = "Student" });
        using var student = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = student.RootElement.GetProperty("id").GetGuid();
        Assert.That((await client.PostAsJsonAsync("/api/users/login", new { email = "admin@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(client);
        var target = await client.GetAsync($"/api/admin/users/{id}");
        var etag = target.Headers.ETag?.ToString();
        var missing = await client.PutAsJsonAsync($"/api/admin/users/{id}/email", new { email = "new@example.it" });
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{id}/email") { Content = JsonContent.Create(new { email = "new@example.it" }) };
        update.Headers.TryAddWithoutValidation("If-Match", etag);
        var updated = await client.SendAsync(update);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var changed = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.That(changed.RootElement.GetProperty("id").GetGuid(), Is.EqualTo(id));
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{id}/role") { Content = JsonContent.Create(new { role = "Teacher" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.That((await client.SendAsync(stale)).StatusCode, Is.EqualTo(HttpStatusCode.PreconditionFailed));
    }

    [Test]
    public async Task AC2_PoliciesRejectPendingTeacherAndDoNotInheritAdminPrivileges()
    {
        using var client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUsersService>();
        var auth = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        var (_, admin) = await users.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await users.Execute(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
        var adminPrincipal = CoursePilot.Host.Modules.Users.Transports.Http.UsersCookieEvents.CreatePrincipal(admin.Id, admin.SessionRevision);
        var teacherPrincipal = CoursePilot.Host.Modules.Users.Transports.Http.UsersCookieEvents.CreatePrincipal(teacher.Id, teacher.SessionRevision);
        Assert.That((await auth.AuthorizeAsync(adminPrincipal, null, "Admin")).Succeeded, Is.True);
        Assert.That((await auth.AuthorizeAsync(adminPrincipal, null, "ApprovedTeacher")).Succeeded, Is.False);
        Assert.That((await auth.AuthorizeAsync(adminPrincipal, null, "Student")).Succeeded, Is.False);
        Assert.That((await auth.AuthorizeAsync(teacherPrincipal, null, "ApprovedTeacher")).Succeeded, Is.False);
        await users.Execute(new ApproveTeacherCommand(admin.Id, teacher.Id, context.Version));
        Assert.That((await auth.AuthorizeAsync(teacherPrincipal, null, "ApprovedTeacher")).Succeeded, Is.True);
    }

    [Test]
    public async Task SwaggerDocumentIsServed()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/api/users/register").GetProperty("post").GetProperty("parameters");
        Assert.That(parameters.EnumerateArray().Any(p => p.GetProperty("name").GetString() == "X-CSRF-TOKEN"), Is.True);
    }

    private static async Task Token(HttpClient client)
    {
        var response = await client.GetAsync("/api/users/antiforgery");
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", data.RootElement.GetProperty("token").GetString());
    }
}
