using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Locations;

/// <summary>
/// Resuelve en qué sede está operando el usuario (ADR-008).
///
/// <para>Es el único lugar donde se decide esa pregunta, para que todas las estaciones la contesten
/// igual. El orden es deliberado: lo que el usuario eligió explícitamente gana sobre cualquier
/// inferencia, porque el que trabaja en dos sedes tiene que poder decir en cuál está parado.</para>
///
/// <list type="number">
///   <item>La sede pedida en el request, si el usuario tiene acceso a ella.</item>
///   <item>Su sede primaria.</item>
///   <item>Su única sede, si tiene una sola asignada.</item>
///   <item>La sede principal de la organización.</item>
/// </list>
///
/// <para>Un usuario sin asignaciones queda asignado a la sede principal en el acto (FAC-9): nunca
/// opera "en todas".</para>
/// </summary>
public sealed class ResolveActiveLocationHandler(
    ILocationRepository locations,
    ILocationProvisioning provisioning,
    IRequestLocationContext requestContext)
{
    public async Task<Guid> HandleAsync(
        Guid organizationId,
        Guid? userId,
        Guid? requestedLocationId = null,
        CancellationToken ct = default)
    {
        // La sede declarada en el request (selector de sede activa) vale como elección explícita
        // del usuario. Es una declaración, no una autorización: abajo se verifica el acceso.
        requestedLocationId ??= requestContext.RequestedLocationId;

        var assignments = userId is null
            ? []
            : await locations.ListForUserAsync(organizationId, userId.Value, ct);

        // Todo usuario tiene sede (FAC-9). Uno que llega sin ninguna queda en la principal antes de
        // decidir nada: si no, "sin asignaciones" equivalía a "todas", y la sede decide con qué
        // razón social se factura lo que atiende.
        if (userId is { } usuario && assignments.Count == 0
            && await provisioning.EnsureUserAssignedAsync(organizationId, usuario, null, ct))
        {
            assignments = await locations.ListForUserAsync(organizationId, usuario, ct);
        }

        if (requestedLocationId is { } requested && requested != Guid.Empty)
        {
            var target = await locations.GetAsync(organizationId, requested, ct)
                ?? throw new ArgumentException("La sede indicada no existe en esta organización.", nameof(requestedLocationId));
            if (!target.Active)
            {
                throw new ArgumentException("La sede indicada está desactivada.", nameof(requestedLocationId));
            }
            // Solo en sus sedes. (Sin usuario —procesos internos— no hay a quién restringir.)
            if (userId is not null && !assignments.Any(a => a.Location.Id == requested))
            {
                throw new ArgumentException("No tenés asignada esa sede.", nameof(requestedLocationId));
            }
            return target.Id;
        }

        var primary = assignments.FirstOrDefault(a => a.Assignment.IsPrimary);
        if (primary.Location is not null)
        {
            return primary.Location.Id;
        }
        if (assignments.Count == 1)
        {
            return assignments[0].Location.Id;
        }

        var fallback = await locations.GetDefaultAsync(organizationId, ct)
            ?? await provisioning.EnsureDefaultLocationAsync(organizationId, null, ct);
        return fallback.Id;
    }

    /// <summary>
    /// La sede activa como filtro de listas operativas: lo que una sucursal no debería ver mezclado
    /// con lo de otra (worklists del laboratorio).
    /// </summary>
    public async Task<ActiveLocationScope> ResolveScopeAsync(
        Guid organizationId, Guid? userId, CancellationToken ct = default)
    {
        var id = await HandleAsync(organizationId, userId, null, ct);
        var sede = await locations.GetAsync(organizationId, id, ct);
        return new ActiveLocationScope(id, sede?.IsDefault ?? false);
    }
}

/// <summary>Sede activa, con lo necesario para decidir si un dato es de ella.</summary>
public sealed record ActiveLocationScope(Guid LocationId, bool IsDefault)
{
    /// <summary>
    /// Si un dato con esa sede es de la activa. Lo anterior al eje de sede no tiene ninguna: cuenta
    /// como de la principal, que es donde <c>LocationBackfill</c> ancla los datos viejos.
    /// </summary>
    public bool Includes(Guid? locationId) => locationId is { } id ? id == LocationId : IsDefault;
}
