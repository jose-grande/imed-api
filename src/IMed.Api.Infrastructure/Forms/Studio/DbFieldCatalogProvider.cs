using System.Text.Json;
using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Forms.Studio;
using IMed.Api.Domain.Forms;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Forms.Studio;

/// <summary>
/// Lee los catálogos de <c>form_catalogs</c> y sus campos de <c>form_catalog_fields</c>. Por cada tipo
/// de formulario usa la versión activa más alta y sus campos activos, en su orden. Guarda una copia en
/// memoria durante <see cref="CacheDuration"/>, así un catálogo cargado con los scripts de
/// <c>db/forms</c> se ve en la API como máximo un minuto después.
/// </summary>
public sealed class DbFieldCatalogProvider(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<DbFieldCatalogProvider> logger) : IFieldCatalogProvider, IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Snapshot? snapshot;

    private sealed record Entry(FormCatalogSummary Summary, FieldCatalog Catalog, string Json);

    private sealed record Snapshot(DateTimeOffset LoadedAt, IReadOnlyDictionary<string, Entry> ByCode);

    public async Task<IReadOnlyList<FormCatalogSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetSnapshotAsync(cancellationToken);
        return current.ByCode.Values.Select(e => e.Summary).OrderBy(s => s.FormCode, StringComparer.Ordinal).ToList();
    }

    public async Task<FieldCatalog?> FindAsync(string formCode, CancellationToken cancellationToken = default)
    {
        var current = await GetSnapshotAsync(cancellationToken);
        return current.ByCode.TryGetValue(formCode, out var entry) ? entry.Catalog : null;
    }

    public async Task<string?> GetCatalogJsonAsync(string formCode, CancellationToken cancellationToken = default)
    {
        var current = await GetSnapshotAsync(cancellationToken);
        return current.ByCode.TryGetValue(formCode, out var entry) ? entry.Json : null;
    }

    public void Invalidate() => snapshot = null;

    public void Dispose() => gate.Dispose();

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var current = snapshot;
        if (current is not null && timeProvider.GetUtcNow() - current.LoadedAt < CacheDuration)
        {
            return current;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            current = snapshot;
            if (current is not null && timeProvider.GetUtcNow() - current.LoadedAt < CacheDuration)
            {
                return current;
            }

            current = await LoadAsync(cancellationToken);
            snapshot = current;
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Snapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalogs = await db.FormCatalogs.AsNoTracking().Where(c => c.Active).ToListAsync(cancellationToken);

        // Solo se cargan los campos de la versión que se va a usar de cada tipo.
        var latestByCode = catalogs
            .GroupBy(c => c.FormCode)
            .Select(group => group
                .OrderByDescending(c => Version.TryParse(c.Version, out var parsed) ? parsed : new Version(0, 0))
                .First())
            .ToList();
        var ids = latestByCode.Select(c => c.Id).ToList();

        var fieldRows = await db.FormCatalogFields.AsNoTracking()
            .Where(f => f.Active && ids.Contains(f.CatalogId))
            .OrderBy(f => f.SortOrder)
            .ToListAsync(cancellationToken);
        var fieldsByCatalog = fieldRows.ToLookup(f => f.CatalogId);

        var byCode = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var latest in latestByCode)
        {
            // Un catálogo mal formado no debe tumbar a los demás: se omite y se registra.
            try
            {
                byCode[latest.FormCode] = ToEntry(latest, fieldsByCatalog[latest.Id].ToList());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                logger.LogError(ex, "Catálogo '{FormCode}' {Version} mal formado; se omite.", latest.FormCode, latest.Version);
            }
        }

        return new Snapshot(timeProvider.GetUtcNow(), byCode);
    }

    private static Entry ToEntry(FormCatalog row, List<FormCatalogField> fieldRows)
    {
        if (fieldRows.Count == 0)
        {
            throw new InvalidOperationException("El catálogo no tiene campos activos.");
        }

        // El catálogo se sirve con la misma forma de siempre: un arreglo de campos. Las banderas en
        // falso y los datos vacíos se omiten.
        var fieldsJson = new JsonArray();
        foreach (var field in fieldRows)
        {
            var node = new JsonObject { ["key"] = field.FieldKey, ["widget"] = field.Widget };
            if (field.Core)
            {
                node["core"] = true;
            }
            if (field.Custom)
            {
                node["custom"] = true;
            }
            node["defaults"] = JsonNode.Parse(field.Defaults);
            node["editable"] = JsonNode.Parse(field.Editable);
            if (field.Messages is not null)
            {
                node["messages"] = JsonNode.Parse(field.Messages);
            }
            if (field.Email)
            {
                node["email"] = true;
            }
            if (field.ListSource is not null)
            {
                node["list"] = JsonNode.Parse(field.ListSource);
            }
            node["binding"] = JsonNode.Parse(field.Binding);
            fieldsJson.Add(node);
        }

        var fields = fieldsJson.Deserialize<List<CatalogField>>(JsonOptions)
            ?? throw new InvalidOperationException("Campos vacíos.");
        var resourceTypes = JsonSerializer.Deserialize<List<string>>(row.ResourceTypes, JsonOptions) ?? [];

        var json = new JsonObject
        {
            ["form"] = row.FormCode,
            ["version"] = row.Version,
            ["title"] = row.Title,
            ["resourceTypes"] = JsonNode.Parse(row.ResourceTypes),
            ["fields"] = fieldsJson,
        }.ToJsonString();

        return new Entry(
            new FormCatalogSummary(row.FormCode, row.Version, row.Title, resourceTypes),
            new FieldCatalog(row.FormCode, row.Version, fields),
            json);
    }
}
