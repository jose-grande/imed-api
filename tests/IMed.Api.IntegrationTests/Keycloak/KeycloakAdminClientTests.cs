using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Keycloak;

/// <summary>
/// Tests integración de los 3 métodos de <see cref="IKeycloakAdminClient"/>
/// agregados en Épica F (F1.4): <c>CreateUserAsync</c>,
/// <c>GetUserByEmailAsync</c>, <c>ExecuteActionsEmailAsync</c>. Validan que el
/// cliente formula bien las peticiones contra el WireMock de Keycloak.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class KeycloakAdminClientTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IKeycloakAdminClient ResolveAdmin(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IKeycloakAdminClient>();

    [Fact]
    public async Task CreateUserAsync_devuelveElIdGeneradoPorKeycloak()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var admin = ResolveAdmin(scope);

        var request = new CreateKeycloakUserRequest
        {
            Email = "alice@imed.test",
            Username = "alice",
            FirstName = "Alice",
            LastName = "Smith",
            Enabled = true,
            EmailVerified = false,
            RequiredActions = new[] { "UPDATE_PASSWORD", "VERIFY_EMAIL" },
            Attributes = new Dictionary<string, string[]>
            {
                ["organization_id"] = new[] { "org-1" },
            },
        };

        var userId = await admin.CreateUserAsync(request);

        userId.Should().NotBe(Guid.Empty);
        fixture.Keycloak.CreateUserCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetUserByEmailAsync_devuelveNullCuandoNoExiste()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var admin = ResolveAdmin(scope);

        var snapshot = await admin.GetUserByEmailAsync("noexiste@imed.test");

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task GetUserByEmailAsync_encuentraUserCreadoPreviamente()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var admin = ResolveAdmin(scope);

        var created = await admin.CreateUserAsync(new CreateKeycloakUserRequest
        {
            Email = "bob@imed.test",
            Username = "bob",
        });

        var snapshot = await admin.GetUserByEmailAsync("bob@imed.test");

        snapshot.Should().NotBeNull();
        snapshot!.Id.Should().Be(created);
        snapshot.Email.Should().Be("bob@imed.test");
        snapshot.Username.Should().Be("bob");
    }

    [Fact]
    public async Task ExecuteActionsEmailAsync_pasaActionsLifespanRedirectClientALaQuery()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var admin = ResolveAdmin(scope);

        var userId = await admin.CreateUserAsync(new CreateKeycloakUserRequest
        {
            Email = "carol@imed.test",
            Username = "carol",
        });

        var actions = new[] { "UPDATE_PASSWORD", "VERIFY_EMAIL" };
        await admin.ExecuteActionsEmailAsync(
            userId,
            actions,
            lifespanSeconds: 604800,
            redirectUri: "http://localhost:4200",
            clientId: "imed-clinical-dev-portal");

        fixture.Keycloak.ExecuteActionsEmailCallCount.Should().Be(1);
        var call = fixture.Keycloak.LastExecuteActionsEmail;
        call.Should().NotBeNull();
        call!.UserId.Should().Be(userId);
        call.Actions.Should().BeEquivalentTo(actions);
        call.LifespanSeconds.Should().Be(604800);
        call.RedirectUri.Should().Be("http://localhost:4200");
        call.ClientId.Should().Be("imed-clinical-dev-portal");
    }
}
