using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Authorization;

public sealed class AuthenticationFlowTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const string Email = "operator@smartline.test";
    private const string Password = "Integration!12345";

    [Fact]
    public async Task Login_issues_a_working_access_token_and_hardened_refresh_cookie()
    {
        await Fixture.CreateIdentityUserAsync(Email, Password, "Operator");
        using var client = Fixture.CreateAnonymousClient();

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = Email, password = Password }
        );
        var data = await AssertSuccessAsync(login);
        var accessToken = data.GetProperty("accessToken").GetString();

        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.Contains(
            "Operator",
            data.GetProperty("user").GetProperty("roles").EnumerateArray().Select(x => x.GetString())
        );
        var refreshCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("smartline.refresh=", refreshCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refreshCookie, StringComparison.OrdinalIgnoreCase);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken
        );
        var me = await client.GetAsync("/api/auth/me");
        var currentUser = await AssertSuccessAsync(me);
        Assert.Equal(Email, currentUser.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Refresh_rotates_the_cookie_and_rejects_replay_of_the_old_token()
    {
        await Fixture.CreateIdentityUserAsync(Email, Password, "Viewer");
        using var client = Fixture.CreateAnonymousClient();

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = Email, password = Password }
        );
        await AssertSuccessAsync(login);
        var originalCookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];

        var refresh = await client.PostAsync("/api/auth/refresh", null);
        await AssertSuccessAsync(refresh);
        var replacementCookie = Assert.Single(refresh.Headers.GetValues("Set-Cookie")).Split(';')[0];
        Assert.NotEqual(originalCookie, replacementCookie);

        using var replayClient = Fixture.CreateAnonymousClient();
        replayClient.DefaultRequestHeaders.Add("Cookie", originalCookie);
        var replay = await replayClient.PostAsync("/api/auth/refresh", null);
        await AssertErrorAsync(
            replay,
            HttpStatusCode.Unauthorized,
            "AUTHENTICATION_REQUIRED"
        );
    }

    [Fact]
    public async Task Replaying_a_rotated_refresh_token_revokes_the_users_other_active_tokens()
    {
        await Fixture.CreateIdentityUserAsync(Email, Password, "Viewer");
        using var client = Fixture.CreateAnonymousClient();

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = Email, password = Password }
        );
        await AssertSuccessAsync(login);
        var originalCookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];

        var refresh = await client.PostAsync("/api/auth/refresh", null);
        await AssertSuccessAsync(refresh);
        var latestCookie = Assert.Single(refresh.Headers.GetValues("Set-Cookie")).Split(';')[0];

        using var replayClient = Fixture.CreateAnonymousClient();
        replayClient.DefaultRequestHeaders.Add("Cookie", originalCookie);
        var replay = await replayClient.PostAsync("/api/auth/refresh", null);
        await AssertErrorAsync(replay, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");

        using var latestClient = Fixture.CreateAnonymousClient();
        latestClient.DefaultRequestHeaders.Add("Cookie", latestCookie);
        var refreshAfterReuse = await latestClient.PostAsync("/api/auth/refresh", null);
        await AssertErrorAsync(
            refreshAfterReuse,
            HttpStatusCode.Unauthorized,
            "AUTHENTICATION_REQUIRED"
        );
    }

    [Fact]
    public async Task Invalid_credentials_return_the_authentication_error_contract()
    {
        await Fixture.CreateIdentityUserAsync(Email, Password, "Viewer");
        using var client = Fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = Email, password = "incorrect-password" }
        );

        await AssertErrorAsync(
            response,
            HttpStatusCode.Unauthorized,
            "AUTHENTICATION_REQUIRED"
        );
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        await Fixture.CreateIdentityUserAsync(Email, Password, "Viewer");
        using var client = Fixture.CreateAnonymousClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = Email, password = Password }
        );
        var loginData = await AssertSuccessAsync(login);
        var refreshCookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginData.GetProperty("accessToken").GetString()
        );

        var logout = await client.PostAsync("/api/auth/logout", null);
        await AssertSuccessAsync(logout);

        using var replay = Fixture.CreateAnonymousClient();
        replay.DefaultRequestHeaders.Add("Cookie", refreshCookie);
        var refresh = await replay.PostAsync("/api/auth/refresh", null);
        await AssertErrorAsync(
            refresh,
            HttpStatusCode.Unauthorized,
            "AUTHENTICATION_REQUIRED"
        );
    }

    [Fact]
    public async Task Admin_can_create_a_user_and_change_their_role()
    {
        using var admin = Fixture.CreateUserClient("Admin");
        const string managedEmail = "managed@smartline.test";
        var createdResponse = await admin.PostAsJsonAsync(
            "/api/admin/users",
            new { email = managedEmail, password = Password, role = "Viewer" }
        );
        var created = await AssertSuccessAsync(createdResponse, HttpStatusCode.Created);
        var userId = created.GetProperty("id").GetGuid();
        Assert.Contains(
            "Viewer",
            created.GetProperty("roles").EnumerateArray().Select(role => role.GetString())
        );

        var roleResponse = await admin.PutAsJsonAsync(
            $"/api/admin/users/{userId}/role",
            new { role = "Operator" }
        );
        var changed = await AssertSuccessAsync(roleResponse);
        Assert.Contains(
            "Operator",
            changed.GetProperty("roles").EnumerateArray().Select(role => role.GetString())
        );

        using var loginClient = Fixture.CreateAnonymousClient();
        var login = await loginClient.PostAsJsonAsync(
            "/api/auth/login",
            new { email = managedEmail, password = Password }
        );
        var token = await AssertSuccessAsync(login);
        Assert.Contains(
            "Operator",
            token
                .GetProperty("user")
                .GetProperty("roles")
                .EnumerateArray()
                .Select(role => role.GetString())
        );
    }

    [Fact]
    public async Task Last_admin_cannot_be_demoted()
    {
        var onlyAdmin = await Fixture.CreateIdentityUserAsync(
            "only-admin@smartline.test",
            Password,
            "Admin"
        );
        using var client = Fixture.CreateUserClient("Admin");

        var response = await client.PutAsJsonAsync(
            $"/api/admin/users/{onlyAdmin.Id}/role",
            new { role = "Viewer" }
        );

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "LAST_ADMIN_REQUIRED");
        var adminCount = await Fixture.WithDbContextAsync(async db =>
        {
            var adminRoleId = await db
                .Roles.Where(role => role.Name == "Admin")
                .Select(role => role.Id)
                .SingleAsync();
            return await db.UserRoles.CountAsync(item => item.RoleId == adminRoleId);
        });
        Assert.Equal(1, adminCount);
    }

    [Fact]
    public async Task Concurrent_admin_demotions_preserve_one_admin()
    {
        var first = await Fixture.CreateIdentityUserAsync(
            "first-admin@smartline.test",
            Password,
            "Admin"
        );
        var second = await Fixture.CreateIdentityUserAsync(
            "second-admin@smartline.test",
            Password,
            "Admin"
        );
        using var firstClient = Fixture.CreateUserClient("Admin");
        using var secondClient = Fixture.CreateUserClient("Admin");

        var responses = await Task.WhenAll(
            firstClient.PutAsJsonAsync(
                $"/api/admin/users/{first.Id}/role",
                new { role = "Viewer" }
            ),
            secondClient.PutAsJsonAsync(
                $"/api/admin/users/{second.Id}/role",
                new { role = "Viewer" }
            )
        );

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var rejected = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict
        );
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "LAST_ADMIN_REQUIRED");

        var adminCount = await Fixture.WithDbContextAsync(async db =>
        {
            var adminRoleId = await db
                .Roles.Where(role => role.Name == "Admin")
                .Select(role => role.Id)
                .SingleAsync();
            return await db.UserRoles.CountAsync(item => item.RoleId == adminRoleId);
        });
        Assert.Equal(1, adminCount);
    }
}
