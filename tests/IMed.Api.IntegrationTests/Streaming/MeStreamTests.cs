using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Streaming;

[Collection(ImedApiCollection.Name)]
public sealed class MeStreamTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Subscriber_receives_refresh_token_event_when_outbox_dispatcher_completes()
    {
        // Arrange: usuario que va a hacer bootstrap. Forzamos KC fail 1 vez para que el dispatch
        // pase por el worker (ese es el path que publica al bus).
        // Dos fallos y no uno: el procesador corre cada segundo y puede tomar el evento antes de
        // que el hot-path llegue a Keycloak (el alta aprovisiona razón social y sedes en el medio,
        // FAC-9). Con un solo fallo, el que llega primero lo consume y el otro sincroniza bien; si
        // el que sincroniza es el hot-path, nadie publica. Con dos, el despacho exitoso es siempre
        // el del procesador, que es el camino que esta prueba verifica.
        fixture.Keycloak.EnableFailing(putUserCalls: 2);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "stream.user");
        var token = fixture.Keycloak.IssueUserToken(userId, preferredUsername: "stream.user");

        // 1. Abrir el SSE stream en background.
        using var streamClient = fixture.CreateClientWithToken(token);
        streamClient.Timeout = TimeSpan.FromSeconds(60);

        var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var receivedEvents = new List<(string Event, string Data)>();
        var streamTask = ReadEventsAsync(streamClient, "/api/me/stream", receivedEvents, streamCts.Token);

        // Pequeña espera para que el subscriber esté listo antes del publish.
        await Task.Delay(500);

        // 2. Disparar el bootstrap (KC sync va a fallar 1 vez → worker eventually dispatches).
        using var apiClient = fixture.CreateClientWithToken(token);
        var resp = await apiClient.PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Clínica Stream" });
        resp.EnsureSuccessStatusCode();

        // 3. Esperar a que el bus publique el evento `refresh_token` → llegue al stream.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (receivedEvents.Any(e => e.Event == "refresh_token"))
            {
                break;
            }
            await Task.Delay(100);
        }

        // Assert
        receivedEvents.Should().Contain(e => e.Event == "refresh_token",
            $"el dispatcher publica refresh_token al despachar. recibidos: " +
            string.Join(", ", receivedEvents.Select(e => e.Event)));

        var refresh = receivedEvents.First(e => e.Event == "refresh_token");
        refresh.Data.Should().Contain("organization_assigned");

        // Cerrar el stream.
        streamCts.Cancel();
        try { await streamTask; } catch (OperationCanceledException) { /* ok */ }
    }

    private static async Task ReadEventsAsync(
        HttpClient client,
        string url,
        List<(string Event, string Data)> sink,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            string? currentEvent = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (line.StartsWith(": ", StringComparison.Ordinal)) continue; // heartbeat / connected

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    currentEvent = line.Substring("event: ".Length);
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var data = line.Substring("data: ".Length);
                    if (currentEvent is not null)
                    {
                        sink.Add((currentEvent, data));
                        currentEvent = null;
                    }
                }
                else if (string.IsNullOrEmpty(line))
                {
                    currentEvent = null;
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (HttpRequestException) when (cancellationToken.IsCancellationRequested) { /* shutdown */ }
    }
}
