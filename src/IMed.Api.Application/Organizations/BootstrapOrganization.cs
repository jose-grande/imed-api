using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Subscriptions;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Organizations;

/// <summary>
/// Command del caso de uso "el usuario recién registrado crea su primera
/// Organization". Ver PLAN §2.3.4.
/// </summary>
public sealed record BootstrapOrganizationCommand(
    Guid KeycloakUserId,
    string OrganizationName,
    string? NationalTaxId);

public sealed record BootstrapOrganizationResult(
    Guid OrganizationId,
    string OrganizationFhirId,
    SubscriptionTier AssignedTier,
    bool PendingKeycloakSync);

public sealed class BootstrapOrganizationHandler(
    IKeycloakAdminClient keycloakAdmin,
    IHapiFhirClient hapiFhir,
    IBootstrapOrganizationPersistence persistence,
    ILabCatalogProvisioning labCatalogProvisioning,
    ILogger<BootstrapOrganizationHandler> logger,
    TimeProvider timeProvider)
{
    private const string OrgAdminRole = "org_admin";

    /// <summary>
    /// Ejecuta el bootstrap end-to-end con el modelo "outbox como red de seguridad":
    /// <list type="number">
    ///   <item>Provisiona la partition en HAPI y persiste la Organization FHIR.</item>
    ///   <item>Persiste subscription + tier assignment + outbox event + audit log
    ///         en una sola transacción local.</item>
    ///   <item>Intenta sincronizar atributos+rol del usuario en Keycloak (hot-path).</item>
    ///   <item>Si la sync KC OK → marca el outbox event dispatched in-line y el
    ///         usuario puede re-loguearse de inmediato. Si la sync KC falla →
    ///         deja el outbox pendiente para que el <c>OutboxProcessor</c> retire
    ///         con backoff. La respuesta lleva <c>PendingKeycloakSync=true</c>.</item>
    /// </list>
    /// Esto preserva la UX del happy-path actual y agrega robustez para fallas
    /// transitorias de Keycloak. Cierra la deuda transaccional documentada en
    /// EPICA-B §3.6.3.2.
    /// </summary>
    public async Task<BootstrapOrganizationResult> HandleAsync(
        BootstrapOrganizationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.OrganizationName))
        {
            throw new ArgumentException("OrganizationName es obligatorio.", nameof(command));
        }

        // 1. Generar el organization_id (UUIDv7) localmente. Lo usamos como
        // nombre de particion HAPI y como claim `organization_id` en Keycloak.
        var now = timeProvider.GetUtcNow();
        var organizationId = Guid.CreateVersion7(now);
        var partitionName = $"org-{organizationId}";

        // 2. Crear la particion HAPI ANTES de crear el Organization.
        // El interceptor de HAPI requiere que la particion exista para escribir.
        await hapiFhir.CreatePartitionAsync(partitionName, cancellationToken);

        // 3. Crear Organization en HAPI dentro de su nueva particion.
        var orgFhirId = await hapiFhir.CreateOrganizationAsync(
            new CreateOrganizationRequest(
                Name: command.OrganizationName,
                NationalTaxId: command.NationalTaxId,
                PartitionName: partitionName),
            cancellationToken);

        // 4. Persistir en imed_app (transacción): subscription + tier assignment +
        //    outbox event + audit log "bootstrap_started".
        var outboxEventId = await persistence.BootstrapAsync(new BootstrapPersistenceInput(
            OrganizationId: organizationId,
            OrganizationName: command.OrganizationName,
            OrganizationFhirId: orgFhirId,
            KeycloakUserId: command.KeycloakUserId,
            Tier: SubscriptionTier.Basic,
            AssignedBy: command.KeycloakUserId,
            RoleName: OrgAdminRole,
            CreatedAt: now),
            cancellationToken);

        // 4.5 Aprovisionar el catálogo de laboratorio (copy-on-create, rev 53):
        //      clona las plantillas globales al catálogo propio de la org. Resiliente —
        //      si falla, el LabCatalogProvisioningBackfill lo recupera al próximo arranque;
        //      no debe tumbar la creación de la org.
        try
        {
            await labCatalogProvisioning.ProvisionOrganizationAsync(organizationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Aprovisionamiento del catálogo de laboratorio falló para org {OrgId}; " +
                "el backfill de arranque lo recuperará.", organizationId);
        }

        // 5. Hot-path: sincronizar Keycloak. Si falla, el OutboxProcessor lo
        //    retomará. NO propagamos la excepción para que el cliente reciba
        //    una respuesta exitosa con flag indicando sync pendiente.
        try
        {
            await SyncKeycloakAsync(command.KeycloakUserId, organizationId, SubscriptionTier.Basic, cancellationToken);

            // Sync OK: marcar el outbox event dispatched para que el worker no
            // duplique el trabajo. Idempotente con la dispatch del worker.
            await persistence.MarkOutboxDispatchedAsync(outboxEventId, cancellationToken);

            logger.LogInformation(
                "Bootstrap completado (sync-path). user={UserId}, organization={OrgId}, fhir={FhirId}",
                command.KeycloakUserId, organizationId, orgFhirId);

            return new BootstrapOrganizationResult(
                organizationId, orgFhirId, SubscriptionTier.Basic, PendingKeycloakSync: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Bootstrap completado parcialmente (KC sync fallo, outbox pendiente). " +
                "user={UserId}, organization={OrgId}, outboxEventId={OutboxId}",
                command.KeycloakUserId, organizationId, outboxEventId);

            return new BootstrapOrganizationResult(
                organizationId, orgFhirId, SubscriptionTier.Basic, PendingKeycloakSync: true);
        }
    }

    private async Task SyncKeycloakAsync(
        Guid keycloakUserId,
        Guid organizationId,
        SubscriptionTier tier,
        CancellationToken cancellationToken)
    {
        await keycloakAdmin.SetUserAttributesAsync(
            keycloakUserId,
            new Dictionary<string, string[]>
            {
                ["organization_id"] = [organizationId.ToString()],
                ["subscription_tier"] = [tier.ToClaimValue()]
            },
            cancellationToken);

        await keycloakAdmin.AssignRealmRoleAsync(keycloakUserId, OrgAdminRole, cancellationToken);
    }
}

/// <summary>Puerto hacia la capa de persistencia para este caso de uso.</summary>
public interface IBootstrapOrganizationPersistence
{
    /// <summary>
    /// Persiste subscription + user-tier assignment + outbox event + audit log
    /// dentro de una sola transacción. Devuelve el id del outbox event creado
    /// para que el handler pueda marcarlo dispatched al completar la sincronización
    /// con Keycloak (hot-path) o dejarlo pendiente para el worker.
    /// </summary>
    Task<Guid> BootstrapAsync(BootstrapPersistenceInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca un outbox event como dispatched (sin payload extra). Lo usa el
    /// hot-path del bootstrap cuando la sincronización Keycloak completa OK,
    /// y también el worker tras un dispatch exitoso.
    /// </summary>
    Task MarkOutboxDispatchedAsync(Guid outboxEventId, CancellationToken cancellationToken = default);
}

public sealed record BootstrapPersistenceInput(
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationFhirId,
    Guid KeycloakUserId,
    SubscriptionTier Tier,
    Guid AssignedBy,
    string RoleName,
    DateTimeOffset CreatedAt);
