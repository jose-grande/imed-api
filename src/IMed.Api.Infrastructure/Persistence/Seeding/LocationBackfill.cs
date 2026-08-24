using IMed.Api.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Backfill del eje de sede (ADR-008): garantiza que toda organización existente tenga su sede
/// principal, y ancla a ella los datos operativos anteriores al eje.
///
/// <para>Corre al arranque y es idempotente. El bootstrap de una organización nueva ya crea su
/// sede en línea; esto recupera a las que se crearon antes de que el eje existiera — que al
/// momento de escribir esto son todas.</para>
/// </summary>
public sealed class LocationBackfill(
    IServiceScopeFactory scopeFactory,
    ILogger<LocationBackfill> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provisioning = scope.ServiceProvider.GetRequiredService<ILocationProvisioning>();

            var orgs = await db.OrganizationSubscriptions.AsNoTracking()
                .Select(x => new { x.OrganizationId, x.Name })
                .ToListAsync(cancellationToken);

            var anchored = 0;
            foreach (var org in orgs)
            {
                try
                {
                    var location = await provisioning.EnsureDefaultLocationAsync(
                        org.OrganizationId, org.Name, cancellationToken);

                    // Datos operativos anteriores al eje: se anclan a la sede principal. Es la
                    // lectura correcta — se generaron cuando la organización operaba en un solo
                    // lugar, aunque nadie lo hubiera modelado.
                    anchored += await AnchorLegacyRowsAsync(db, org.OrganizationId, location.Id, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Una org que falla no debe frenar al resto; el próximo arranque reintenta.
                    logger.LogError(ex,
                        "Backfill de sede falló para org {OrgId}; se reintentará al próximo arranque.",
                        org.OrganizationId);
                }
            }

            if (anchored > 0)
            {
                logger.LogInformation("Backfill de sede: {Count} fila(s) operativa(s) ancladas a su sede principal.", anchored);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LocationBackfill falló; puede haber organizaciones sin sede principal.");
        }
    }

    /// <summary>
    /// Ancla en bloque las filas sin sede. Se hace por SQL y no por el modelo porque son
    /// actualizaciones masivas de datos históricos: no hay invariante de dominio que aplicar,
    /// y materializar las entidades para tocarles una columna sería gratuito solo en tiempo.
    /// </summary>
    private static async Task<int> AnchorLegacyRowsAsync(
        AppDbContext db, Guid organizationId, Guid locationId, CancellationToken ct)
    {
        var total = 0;
        total += await db.Database.ExecuteSqlAsync(
            $"UPDATE order_intake SET location_id = {locationId} WHERE organization_id = {organizationId} AND location_id IS NULL", ct);
        total += await db.Database.ExecuteSqlAsync(
            $"UPDATE collection_plan SET location_id = {locationId} WHERE organization_id = {organizationId} AND location_id IS NULL", ct);
        total += await db.Database.ExecuteSqlAsync(
            $"UPDATE lab_order_checkin SET location_id = {locationId} WHERE organization_id = {organizationId} AND location_id IS NULL", ct);

        total += await RekeyAccessionCounterAsync(db, organizationId, locationId, ct);
        total += await RekeyOrderCounterAsync(db, organizationId, locationId, ct);
        return total;
    }

    /// <summary>
    /// Re-clava los contadores del sentinel (guid cero que dejó la migración) a la sede principal.
    ///
    /// <para><b>Por qué con GREATEST y no con un UPDATE.</b> Los correlativos son diarios: si hoy
    /// ya se emitieron accessions con la secuencia vieja y el contador arrancara de cero para la
    /// sede, se reemitirían números ya impresos en tubos. Tomar el máximo entre lo que había y lo
    /// que hubiera garantiza que ninguna secuencia retroceda.</para>
    /// </summary>
    private static async Task<int> RekeyAccessionCounterAsync(
        AppDbContext db, Guid organizationId, Guid locationId, CancellationToken ct)
    {
        var sentinel = Guid.Empty;
        var moved = await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO lab_accession_counter (organization_id, location_id, day, seq)
             SELECT organization_id, {locationId}, day, seq
               FROM lab_accession_counter
              WHERE organization_id = {organizationId} AND location_id = {sentinel}
             ON CONFLICT (organization_id, location_id, day)
             DO UPDATE SET seq = GREATEST(lab_accession_counter.seq, EXCLUDED.seq)
             """, ct);

        if (moved > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM lab_accession_counter WHERE organization_id = {organizationId} AND location_id = {sentinel}",
                ct);
        }
        return moved;
    }

    /// <inheritdoc cref="RekeyAccessionCounterAsync"/>
    private static async Task<int> RekeyOrderCounterAsync(
        AppDbContext db, Guid organizationId, Guid locationId, CancellationToken ct)
    {
        var sentinel = Guid.Empty;
        var moved = await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO lab_order_counter (organization_id, location_id, day, seq)
             SELECT organization_id, {locationId}, day, seq
               FROM lab_order_counter
              WHERE organization_id = {organizationId} AND location_id = {sentinel}
             ON CONFLICT (organization_id, location_id, day)
             DO UPDATE SET seq = GREATEST(lab_order_counter.seq, EXCLUDED.seq)
             """, ct);

        if (moved > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM lab_order_counter WHERE organization_id = {organizationId} AND location_id = {sentinel}",
                ct);
        }
        return moved;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
