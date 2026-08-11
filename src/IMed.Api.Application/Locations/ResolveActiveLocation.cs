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
/// <para>Un usuario sin asignaciones cae en la sede principal en vez de quedar bloqueado: en una
/// clínica de una sola sede nadie va a asignar a nadie, y exigirlo convertiría el eje en un trámite.</para>
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

        if (requestedLocationId is { } requested && requested != Guid.Empty)
        {
            var target = await locations.GetAsync(organizationId, requested, ct)
                ?? throw new ArgumentException("La sede indicada no existe en esta organización.", nameof(requestedLocationId));
            if (!target.Active)
            {
                throw new ArgumentException("La sede indicada está desactivada.", nameof(requestedLocationId));
            }
            // Sin asignaciones el usuario puede operar en cualquier sede de su organización; con
            // asignaciones, solo en las suyas. Así el eje no estorba a la clínica de una sede y
            // sí limita a quien tiene un ámbito definido.
            if (assignments.Count > 0 && !assignments.Any(a => a.Location.Id == requested))
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
}
