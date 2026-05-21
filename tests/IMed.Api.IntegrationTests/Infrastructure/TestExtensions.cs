using System.Net.Http.Headers;
using IMed.Api.Domain.Outbox;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IMed.Api.IntegrationTests.Infrastructure;

public static class TestExtensions
{
    public static HttpClient CreateClientWithToken(this ImedApiFixture fixture, string accessToken)
    {
        var client = fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            // Tests no siguen redirects automáticamente.
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Polling helper con timeout. Repite cada 100ms hasta que la condición es true o se agota el timeout.</summary>
    public static async Task<T> WaitForAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> predicate,
        TimeSpan timeout,
        string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        T last = default!;
        while (DateTime.UtcNow < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Timed out waiting for: {description}. Last value: {last}");
    }

    public static async Task<OutboxEvent?> FindOutboxByTypeAsync(
        this ImedApiFixture fixture, string type)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxEvents
            .Where(x => x.Type == type)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();
    }
}
