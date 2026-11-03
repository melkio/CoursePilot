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
    private WebApplicationFactory<Program> factory = null!; // Initialized by NUnit before each test.
    private MongoClient mongo = null!; // Initialized by NUnit before each test.
    private string db = string.Empty;
    private string uri = string.Empty;

    [SetUp]
    public void Setup()
    {
        uri = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://127.0.0.1:27017/?serverSelectionTimeoutMS=5000";
        db = "coursepilot_http_" + Guid.NewGuid().ToString("N");
        mongo = new MongoClient(uri);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDB:ConnectionString", new MongoUrlBuilder(uri) { DatabaseName = db }.ToString());
        });
    }

    [TearDown]
    public async Task Cleanup()
    {
        try { await factory.DisposeAsync(); await mongo.DropDatabaseAsync(db); }
        finally { mongo.Dispose(); }
    }

    [Test]
    public async Task AC2_TeacherApprovalInvalidatesCookieAndAdminOperationsRequireCsrf()
    {
        using var teacher = factory.CreateClient();
        using var admin = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
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
        await Token(teacher);
        Assert.That((await teacher.PostAsJsonAsync("/api/admin/users/search", new { })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        await Token(admin);
        Assert.That((await admin.PostAsJsonAsync("/api/users/login", new { email = "admin@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(admin);
        var target = await admin.GetAsync($"/api/admin/users/{teacherId}");
        var etag = target.Headers.ETag?.ToString();
        Assert.That(etag, Is.Not.Null);
        using var targetJson = JsonDocument.Parse(await target.Content.ReadAsStringAsync());
        var resourceContext = targetJson.RootElement.GetProperty("context");
        Assert.That(resourceContext.GetProperty("version").GetInt32(), Is.EqualTo(1));
        Assert.That(resourceContext.GetProperty("createdAt").GetDateTimeOffset(), Is.Not.EqualTo(default(DateTimeOffset)));
        Assert.That(resourceContext.GetProperty("updatedAt").GetDateTimeOffset(), Is.Not.EqualTo(default(DateTimeOffset)));
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
        using var client = factory.CreateClient();
        await Token(client);
        var invalid = await client.PostAsJsonAsync("/api/users/register", new { email = "", password = "password123", role = "Student" });
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var spoof = await client.PostAsJsonAsync("/api/users/register", new { email = "person@example.it", password = "password123", role = "Student", teacherApprovalStatus = "Approved" });
        Assert.That(spoof.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var escalation = await client.PostAsJsonAsync("/api/users/register", new { email = "person@example.it", password = "password123", role = "Admin" });
        Assert.That(escalation.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var multiple = await client.PostAsJsonAsync("/api/users/register", new { email = "person@example.it", password = "password123", role = new[] { "Student", "Teacher" } });
        Assert.That(multiple.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AC1_ChangeEmailKeepsIdAndStaleOrMissingIfMatchFails()
    {
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUsersService>().Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
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
        using var renamed = factory.CreateClient();
        await Token(renamed);
        var renamedLogin = await renamed.PostAsJsonAsync("/api/users/login", new { email = "new@example.it", password = "password123" });
        Assert.That(renamedLogin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var renamedMe = JsonDocument.Parse(await (await renamed.GetAsync("/api/users/me")).Content.ReadAsStringAsync());
        Assert.That(renamedMe.RootElement.GetProperty("id").GetGuid(), Is.EqualTo(id));
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{id}/role") { Content = JsonContent.Create(new { role = "Teacher" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.That((await client.SendAsync(stale)).StatusCode, Is.EqualTo(HttpStatusCode.PreconditionFailed));
    }

    [Test]
    public async Task AC2_PoliciesRejectPendingTeacherAndDoNotInheritAdminPrivileges()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUsersService>();
        var auth = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        var (_, admin) = await users.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, teacher) = await users.ExecuteAndGet(new RegisterUserCommand("teacher@example.it", "password123", UserRole.Teacher));
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
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var antiforgery = document.RootElement.GetProperty("paths").GetProperty("/api/users/antiforgery").GetProperty("get");
        var antiforgerySchema = antiforgery.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.That(antiforgerySchema.GetProperty("$ref").GetString(), Does.EndWith("/AntiforgeryTokenModel"));
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/api/users/register").GetProperty("post").GetProperty("parameters");
        Assert.That(parameters.EnumerateArray().Any(p => p.GetProperty("name").GetString() == "X-CSRF-TOKEN"), Is.True);
        var search = document.RootElement.GetProperty("paths").GetProperty("/api/admin/users/search").GetProperty("post");
        Assert.That(search.GetProperty("responses").TryGetProperty("200", out _), Is.True);
        Assert.That(search.GetProperty("responses").TryGetProperty("403", out _), Is.True);
        Assert.That(search.GetProperty("parameters").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "X-CSRF-TOKEN"), Is.True);
        Assert.That(search.GetProperty("parameters").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "If-Match"), Is.False);
        var roleOperation = document.RootElement.GetProperty("paths").GetProperty("/api/admin/users/{id}/role").GetProperty("put");
        Assert.That(roleOperation.GetProperty("responses").TryGetProperty("412", out _), Is.True);
        Assert.That(roleOperation.GetProperty("parameters").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "If-Match"), Is.True);
    }

    [Test]
    public async Task AC1_LoginErrorsAreIndistinguishableAndLogoutRequiresValidCsrf()
    {
        using var client = factory.CreateClient();
        await Token(client);
        Assert.That((await client.PostAsJsonAsync("/api/users/register", new { email = "student@example.it", password = "password123", role = "Student" })).StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var missing = await client.PostAsJsonAsync("/api/users/login", new { email = "missing@example.it", password = "badpassword" });
        var wrong = await client.PostAsJsonAsync("/api/users/login", new { email = "student@example.it", password = "badpassword" });
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(wrong.StatusCode, Is.EqualTo(missing.StatusCode));
        using var first = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        using var second = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync());
        Assert.That(first.RootElement.GetProperty("title").GetString(), Is.EqualTo(second.RootElement.GetProperty("title").GetString()));
        Assert.That((await client.PostAsJsonAsync("/api/users/login", new { email = "student@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        Assert.That((await client.PostAsync("/api/users/logout", null)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await client.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(client);
        Assert.That((await client.PostAsync("/api/users/logout", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await client.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AC2_AdminTransitionsAndDuplicateEmailReturnExpectedStatuses()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUsersService>();
        await users.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, student) = await users.ExecuteAndGet(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        await users.Execute(new RegisterUserCommand("other@example.it", "password123", UserRole.Student));
        await Token(client);
        Assert.That((await client.PostAsJsonAsync("/api/users/login", new { email = "admin@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(client);
        using var duplicate = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{student.Id.Value}/email") { Content = JsonContent.Create(new { email = " OTHER@example.it " }) };
        duplicate.Headers.TryAddWithoutValidation("If-Match", $"\"{context.Version}\"");
        Assert.That((await client.SendAsync(duplicate)).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var invalidApproval = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{student.Id.Value}/approve-teacher");
        invalidApproval.Headers.TryAddWithoutValidation("If-Match", $"\"{context.Version}\"");
        Assert.That((await client.SendAsync(invalidApproval)).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { limit = 101 })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { limit = 0 })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { offset = -1 })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { offset = int.MaxValue, limit = 1 })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { role = "Invalid" })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { role = "Student" })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await Token(client);
        var search = await client.PostAsJsonAsync("/api/admin/users/search", new { role = "Student", limit = 1 });
        Assert.That(search.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var searchResults = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.That(searchResults.RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(searchResults.RootElement[0].GetProperty("role").GetString(), Is.EqualTo("Student"));
        Assert.That(searchResults.RootElement[0].TryGetProperty("passwordHash", out _), Is.False);
        Assert.That((await client.GetAsync($"/api/admin/users/{Guid.NewGuid()}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        foreach (var invalidVersion in new[] { "1", "\"0\"", "\"-1\"", "W/\"1\"", "\"1\", \"2\"", "\" 1\"", "\"x\"" })
        {
            using var malformed = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{student.Id.Value}/role") { Content = JsonContent.Create(new { role = "Teacher" }) };
            malformed.Headers.TryAddWithoutValidation("If-Match", invalidVersion);
            Assert.That((await client.SendAsync(malformed)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidVersion);
        }
        using var role = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{student.Id.Value}/role") { Content = JsonContent.Create(new { role = "Teacher" }) };
        role.Headers.TryAddWithoutValidation("If-Match", $"\"{context.Version}\"");
        var changed = await client.SendAsync(role);
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var data = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.That(data.RootElement.GetProperty("teacherApprovalStatus").GetString(), Is.EqualTo("Pending"));
        Assert.That(changed.Headers.ETag, Is.Not.Null);
    }

    [Test]
    public async Task AC2_NonAdminCannotUseAnyAdminOperationOrSpoofActor()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUsersService>();
        var (_, admin) = await users.ExecuteAndGet(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, student) = await users.ExecuteAndGet(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        await Token(client);
        Assert.That((await client.PostAsJsonAsync("/api/users/login", new { email = "student@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(client);
        Assert.That((await client.PostAsJsonAsync("/api/admin/users/search", new { })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await client.GetAsync($"/api/admin/users/{student.Id.Value}")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        foreach (var (path, body) in new[] { ("email", (object)new { email = "new@example.it", actorId = admin.Id.Value }), ("role", (object)new { role = "Admin", actorId = admin.Id.Value }) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{student.Id.Value}/{path}") { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{context.Version}\"");
            Assert.That((await client.SendAsync(request)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        using var approval = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{student.Id.Value}/approve-teacher");
        approval.Headers.TryAddWithoutValidation("If-Match", $"\"{context.Version}\"");
        Assert.That((await client.SendAsync(approval)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await client.PostAsync("/api/users/bootstrap-admin", null)).StatusCode, Is.AnyOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed));
        Assert.That((await users.Get(student.Id)).User.Role, Is.EqualTo(UserRole.Student));
    }

    [Test]
    public async Task AC2_ChangingRoleInvalidatesExistingStudentSession()
    {
        using var studentClient = factory.CreateClient();
        using var adminClient = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUsersService>();
        await users.Execute(new BootstrapAdminCommand("admin@example.it", "password123"));
        var (context, student) = await users.ExecuteAndGet(new RegisterUserCommand("student@example.it", "password123", UserRole.Student));
        await Token(studentClient);
        Assert.That((await studentClient.PostAsJsonAsync("/api/users/login", new { email = "student@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await studentClient.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await studentClient.GetAsync($"/api/users/{Guid.NewGuid()}")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        await Token(adminClient);
        Assert.That((await adminClient.PostAsJsonAsync("/api/users/login", new { email = "admin@example.it", password = "password123" })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await Token(adminClient);
        using var change = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{student.Id.Value}/role") { Content = JsonContent.Create(new { role = "Teacher" }) };
        change.Headers.TryAddWithoutValidation("If-Match", $"\"{context.Version}\"");
        Assert.That((await adminClient.SendAsync(change)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await studentClient.GetAsync("/api/users/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AC1_IdentityRateLimitIsEnforced()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:IdentityPermitLimit", "2"));
        using var client = limitedFactory.CreateClient();
        await Token(client);
        for (var i = 0; i < 2; i++)
            Assert.That((await client.PostAsJsonAsync("/api/users/login", new { email = "missing@example.it", password = "badpassword" })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await client.PostAsJsonAsync("/api/users/login", new { email = "missing@example.it", password = "badpassword" })).StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
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
