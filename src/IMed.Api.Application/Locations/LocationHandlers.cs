using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Locations;

namespace IMed.Api.Application.Locations;

/// <summary>Sede tal como la ve la interfaz.</summary>
public sealed record LocationDto(
    Guid Id,
    string Code,
    string Name,
    bool IsDefault,
    bool Active,
    string? LocationFhirId)
{
    public static LocationDto FromDomain(OrganizationLocation l) =>
        new(l.Id, l.Code, l.Name, l.IsDefault, l.Active, l.LocationFhirId);
}

/// <summary>Sede del usuario, con la marca de cuál se le ofrece primero.</summary>
public sealed record MyLocationDto(
    Guid Id,
    string Code,
    string Name,
    bool IsDefault,
    bool IsPrimary);

/// <summary>Sedes de la organización. <c>includeInactive</c> solo para administración.</summary>
public sealed class ListLocationsHandler(ILocationRepository locations, ILocationProvisioning provisioning)
{
    public async Task<IReadOnlyList<LocationDto>> HandleAsync(
        Guid organizationId, bool includeInactive = false, CancellationToken ct = default)
    {
        // Una organización sin sede no puede operar; si se llega acá sin ninguna, se crea en el
        // acto en vez de devolver una lista vacía que la interfaz no sabría interpretar.
        var all = await locations.ListAsync(organizationId, activeOnly: !includeInactive, ct);
        if (all.Count == 0)
        {
            await provisioning.EnsureDefaultLocationAsync(organizationId, null, ct);
            all = await locations.ListAsync(organizationId, activeOnly: !includeInactive, ct);
        }
        return all.Select(LocationDto.FromDomain).ToList();
    }
}

/// <summary>
/// Sedes donde el usuario puede operar. Es lo que alimenta el selector de sede activa.
///
/// <para>Un usuario sin asignaciones ve todas las de su organización: en una clínica de una sola
/// sede nadie va a asignar a nadie, y exigirlo convertiría el eje en un trámite.</para>
/// </summary>
public sealed class ListMyLocationsHandler(ILocationRepository locations, ILocationProvisioning provisioning)
{
    public async Task<IReadOnlyList<MyLocationDto>> HandleAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        var assigned = await locations.ListForUserAsync(organizationId, userId, ct);
        if (assigned.Count > 0)
        {
            return assigned
                .Select(x => new MyLocationDto(
                    x.Location.Id, x.Location.Code, x.Location.Name, x.Location.IsDefault, x.Assignment.IsPrimary))
                .ToList();
        }

        var all = await locations.ListAsync(organizationId, activeOnly: true, ct);
        if (all.Count == 0)
        {
            await provisioning.EnsureDefaultLocationAsync(organizationId, null, ct);
            all = await locations.ListAsync(organizationId, activeOnly: true, ct);
        }
        // Sin asignación explícita, la principal de la organización es la que se ofrece primero.
        return all
            .Select(l => new MyLocationDto(l.Id, l.Code, l.Name, l.IsDefault, l.IsDefault))
            .ToList();
    }
}

public sealed record CreateLocationCommand(Guid OrganizationId, string Code, string Name);

public sealed class CreateLocationHandler(ILocationRepository locations, TimeProvider timeProvider)
{
    public async Task<LocationDto> HandleAsync(CreateLocationCommand cmd, CancellationToken ct = default)
    {
        var existing = await locations.GetByCodeAsync(cmd.OrganizationId, cmd.Code, ct);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Ya existe una sede con el código '{existing.Code}'.");
        }

        // La primera sede de una organización es su principal por definición.
        var current = await locations.ListAsync(cmd.OrganizationId, activeOnly: false, ct);
        var location = OrganizationLocation.Create(
            cmd.OrganizationId, cmd.Code, cmd.Name, isDefault: current.Count == 0, timeProvider);

        await locations.AddAsync(location, ct);
        return LocationDto.FromDomain(location);
    }
}

public sealed record UpdateLocationCommand(
    Guid OrganizationId, Guid LocationId, string? Name, bool? Active, bool? MakeDefault);

public sealed class UpdateLocationHandler(ILocationRepository locations, TimeProvider timeProvider)
{
    public async Task<LocationDto?> HandleAsync(UpdateLocationCommand cmd, CancellationToken ct = default)
    {
        var location = await locations.GetAsync(cmd.OrganizationId, cmd.LocationId, ct);
        if (location is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cmd.Name))
        {
            location.Rename(cmd.Name!, timeProvider);
        }

        if (cmd.MakeDefault == true)
        {
            // El índice único filtrado no admite dos principales: hay que soltar la anterior
            // antes de marcar la nueva, y en ese orden.
            var previous = await locations.GetDefaultAsync(cmd.OrganizationId, ct);
            if (previous is not null && previous.Id != location.Id)
            {
                previous.ClearDefault(timeProvider);
                await locations.UpdateAsync(previous, ct);
            }
            location.MarkDefault(timeProvider);
        }

        if (cmd.Active == false)
        {
            location.Deactivate(timeProvider);
        }
        else if (cmd.Active == true)
        {
            location.Reactivate(timeProvider);
        }

        await locations.UpdateAsync(location, ct);
        return LocationDto.FromDomain(location);
    }
}

public sealed record AssignUserToLocationCommand(
    Guid OrganizationId, Guid LocationId, Guid UserId, bool MakePrimary, Guid? AssignedBy);

public sealed class AssignUserToLocationHandler(ILocationRepository locations, TimeProvider timeProvider)
{
    public async Task<bool> HandleAsync(AssignUserToLocationCommand cmd, CancellationToken ct = default)
    {
        var location = await locations.GetAsync(cmd.OrganizationId, cmd.LocationId, ct);
        if (location is null)
        {
            return false;
        }

        var existing = await locations.GetAssignmentAsync(cmd.OrganizationId, cmd.UserId, cmd.LocationId, ct);
        if (existing is null)
        {
            await locations.AddAssignmentAsync(
                UserLocationAssignment.Create(
                    cmd.OrganizationId, cmd.UserId, cmd.LocationId, false, cmd.AssignedBy, timeProvider),
                ct);
        }

        if (cmd.MakePrimary)
        {
            await locations.SetPrimaryAsync(cmd.OrganizationId, cmd.UserId, cmd.LocationId, ct);
        }

        return true;
    }
}

public sealed class UnassignUserFromLocationHandler(ILocationRepository locations)
{
    public Task HandleAsync(Guid organizationId, Guid locationId, Guid userId, CancellationToken ct = default) =>
        locations.RemoveAssignmentAsync(organizationId, userId, locationId, ct);
}
