using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Organizations;

namespace IMed.Api.Application.Locations;

/// <summary>Razón social resumida, tal como acompaña a una sede.</summary>
public sealed record LegalEntitySummaryDto(Guid Id, string LegalName, string? TaxId, bool IsDefault);

/// <summary>Sede tal como la ve la interfaz.</summary>
/// <param name="LocationFhirId">
/// Id del <c>Location</c> FHIR. Es determinístico, así que se devuelve siempre;
/// <paramref name="FhirSynced"/> dice si HAPI ya lo tiene.
/// </param>
public sealed record LocationDto(
    Guid Id,
    string Code,
    string Name,
    bool IsDefault,
    bool Active,
    string? LocationFhirId,
    string? FiscalEstablishmentCode,
    string? Address = null,
    string? Phone = null,
    LegalEntitySummaryDto? LegalEntity = null,
    bool FhirSynced = false)
{
    public static LocationDto FromDomain(OrganizationLocation l, LegalEntity? legalEntity = null) =>
        new(l.Id, l.Code, l.Name, l.IsDefault, l.Active,
            OrganizationLocation.FhirIdFor(l.Id), l.FiscalEstablishmentCode, l.Address, l.Phone,
            legalEntity is null
                ? null
                : new LegalEntitySummaryDto(legalEntity.Id, legalEntity.LegalName, legalEntity.TaxId, legalEntity.IsDefault),
            l.LocationFhirId is not null);
}

/// <summary>Sede del usuario, con la marca de cuál se le ofrece primero.</summary>
public sealed record MyLocationDto(
    Guid Id,
    string Code,
    string Name,
    bool IsDefault,
    bool IsPrimary,
    string? LocationFhirId = null);

/// <summary>Sedes de la organización. <c>includeInactive</c> solo para administración.</summary>
public sealed class ListLocationsHandler(
    ILocationRepository locations, ILegalEntityRepository legalEntities, ILocationProvisioning provisioning)
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

        var razones = (await legalEntities.ListAsync(organizationId, ct)).ToDictionary(r => r.Id);
        return all.Select(l => LocationDto.FromDomain(l, razones.GetValueOrDefault(l.LegalEntityId))).ToList();
    }
}

/// <summary>
/// Sedes donde el usuario puede operar. Es lo que alimenta el selector de sede activa.
///
/// <para><b>Todo usuario tiene sede (FAC-9).</b> Antes, un usuario sin asignaciones veía todas
/// las de su organización. Ahora la sede decide con qué razón social se factura lo que el usuario
/// atiende, así que "todas" ya no es una respuesta neutra: a quien llega sin ninguna se lo asigna
/// a la principal en el acto, y desde ahí la administración decide.</para>
/// </summary>
public sealed class ListMyLocationsHandler(ILocationRepository locations, ILocationProvisioning provisioning)
{
    public async Task<IReadOnlyList<MyLocationDto>> HandleAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        var assigned = await locations.ListForUserAsync(organizationId, userId, ct);
        if (assigned.Count == 0)
        {
            await provisioning.EnsureUserAssignedAsync(organizationId, userId, null, ct);
            assigned = await locations.ListForUserAsync(organizationId, userId, ct);
        }

        // Si tiene asignaciones pero ninguna marcada como principal (dato viejo), la primera hace
        // de principal: el selector necesita una por dónde empezar.
        var hayPrincipal = assigned.Any(x => x.Assignment.IsPrimary);
        return assigned
            .Select((x, i) => new MyLocationDto(
                x.Location.Id, x.Location.Code, x.Location.Name, x.Location.IsDefault,
                x.Assignment.IsPrimary || (!hayPrincipal && i == 0),
                OrganizationLocation.FhirIdFor(x.Location.Id)))
            .ToList();
    }
}

/// <summary>Datos fiscales de una razón social nueva, registrada junto con la sede.</summary>
public sealed record NewLegalEntityInput(
    string LegalName,
    string TaxId,
    string FiscalAddress,
    string? TradeName = null,
    string? Phone = null,
    string? Email = null);

/// <summary>
/// Alta de sede (FAC-9). La razón social se elige de tres formas excluyentes: no decir nada (usa la
/// de la sede principal), <paramref name="LegalEntityId"/> (una que ya existe) o
/// <paramref name="NewLegalEntity"/> (la sede es otra persona jurídica y se registra en el acto).
/// </summary>
public sealed record CreateLocationCommand(
    Guid OrganizationId,
    string Code,
    string Name,
    string? Address = null,
    string? Phone = null,
    Guid? LegalEntityId = null,
    NewLegalEntityInput? NewLegalEntity = null);

/// <summary>
/// Resuelve la razón social de una sede a partir de lo que pidió el usuario. Compartido por el alta
/// y la edición para que las dos apliquen exactamente las mismas reglas.
/// </summary>
public sealed class LegalEntityChoice(
    ILocationRepository locations, ILegalEntityRepository legalEntities, ILocationProvisioning provisioning,
    TimeProvider timeProvider)
{
    public async Task<LegalEntity> ResolveAsync(
        Guid organizationId, Guid? legalEntityId, NewLegalEntityInput? newLegalEntity, CancellationToken ct)
    {
        if (legalEntityId is not null && newLegalEntity is not null)
        {
            throw new ArgumentException(
                "Elegí una razón social existente o registrá una nueva, no las dos cosas.");
        }

        if (newLegalEntity is not null)
        {
            var nueva = LegalEntity.Create(
                organizationId, newLegalEntity.LegalName, newLegalEntity.TaxId, newLegalEntity.FiscalAddress,
                newLegalEntity.TradeName, newLegalEntity.Phone, newLegalEntity.Email, timeProvider);

            // Dos razones sociales con el mismo RTN son la misma persona jurídica cargada dos
            // veces, y partirían sus documentos entre dos libros que el SAR ve como uno.
            var mismoRtn = await legalEntities.GetByTaxIdAsync(organizationId, nueva.TaxId!, ct);
            if (mismoRtn is not null)
            {
                throw new InvalidOperationException(
                    $"Ya existe la razón social '{mismoRtn.LegalName}' con ese RTN: elegila en lugar de registrarla de nuevo.");
            }

            await legalEntities.AddAsync(nueva, ct);
            return nueva;
        }

        if (legalEntityId is { } id)
        {
            var existente = await legalEntities.GetAsync(organizationId, id, ct)
                ?? throw new ArgumentException("La razón social elegida no existe.");
            if (!existente.Active)
            {
                throw new InvalidOperationException("La razón social elegida está inactiva.");
            }
            return existente;
        }

        // Sin elección explícita: la misma persona jurídica que la sede principal. Es el caso de
        // la cadena con un solo RTN y varias sucursales, que es el más común.
        var principal = await locations.GetDefaultAsync(organizationId, ct);
        if (principal is not null)
        {
            var suya = await legalEntities.GetAsync(organizationId, principal.LegalEntityId, ct);
            if (suya is not null)
            {
                return suya;
            }
        }
        return await provisioning.EnsureDefaultLegalEntityAsync(organizationId, null, null, ct);
    }
}

public sealed class CreateLocationHandler(
    ILocationRepository locations, LegalEntityChoice legalEntityChoice, TimeProvider timeProvider)
{
    public async Task<LocationDto> HandleAsync(CreateLocationCommand cmd, CancellationToken ct = default)
    {
        var existing = await locations.GetByCodeAsync(cmd.OrganizationId, cmd.Code, ct);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Ya existe una sede con el código '{existing.Code}'.");
        }

        // Se valida antes de registrar la razón social: un código o nombre vacío no puede dejar
        // atrás una razón social huérfana.
        if (string.IsNullOrWhiteSpace(cmd.Code) || string.IsNullOrWhiteSpace(cmd.Name))
        {
            throw new ArgumentException("El código y el nombre de la sede son obligatorios.");
        }

        var razon = await legalEntityChoice.ResolveAsync(cmd.OrganizationId, cmd.LegalEntityId, cmd.NewLegalEntity, ct);

        // La primera sede de una organización es su principal por definición.
        var current = await locations.ListAsync(cmd.OrganizationId, activeOnly: false, ct);
        var location = OrganizationLocation.Create(
            cmd.OrganizationId, cmd.Code, cmd.Name, razon.Id,
            isDefault: current.Count == 0, address: cmd.Address, phone: cmd.Phone, timeProvider: timeProvider);

        await locations.AddAsync(location, ct);
        return LocationDto.FromDomain(location, razon);
    }
}

/// <param name="Address">Null = no cambia; vacío = se borra.</param>
/// <param name="Phone">Null = no cambia; vacío = se borra.</param>
/// <param name="LegalEntityId">Pasa la sede a otra razón social existente.</param>
/// <param name="NewLegalEntity">Pasa la sede a una razón social nueva, registrada en el acto.</param>
public sealed record UpdateLocationCommand(
    Guid OrganizationId,
    Guid LocationId,
    string? Name,
    bool? Active,
    bool? MakeDefault,
    string? Address = null,
    string? Phone = null,
    Guid? LegalEntityId = null,
    NewLegalEntityInput? NewLegalEntity = null);

public sealed class UpdateLocationHandler(
    ILocationRepository locations,
    ILegalEntityRepository legalEntities,
    IFiscalRepository fiscal,
    LegalEntityChoice legalEntityChoice,
    TimeProvider timeProvider)
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

        if (cmd.Address is not null || cmd.Phone is not null)
        {
            location.UpdateContact(cmd.Address ?? location.Address, cmd.Phone ?? location.Phone, timeProvider);
        }

        if ((cmd.LegalEntityId is { } nuevaId && nuevaId != location.LegalEntityId) || cmd.NewLegalEntity is not null)
        {
            // Los CAI se tramitan a nombre de un RTN. Una sede que cambia de razón social con CAI
            // vigentes seguiría numerando con autorizaciones de otra persona jurídica.
            var vigentes = (await fiscal.ListAuthorizationsAsync(cmd.OrganizationId, ct))
                .Where(a => a.LocationId == location.Id && a.Status == FiscalAuthorizationStatus.Active)
                .ToList();
            if (vigentes.Count > 0)
            {
                throw new InvalidOperationException(
                    $"La sede tiene {vigentes.Count} CAI vigente(s) de su razón social actual. " +
                    "Dalos de baja antes de pasarla a otra razón social.");
            }

            var razonNueva = await legalEntityChoice.ResolveAsync(
                cmd.OrganizationId, cmd.LegalEntityId, cmd.NewLegalEntity, ct);
            location.MoveToLegalEntity(razonNueva.Id, timeProvider);
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
        var razon = await legalEntities.GetAsync(cmd.OrganizationId, location.LegalEntityId, ct);
        return LocationDto.FromDomain(location, razon);
    }
}

public sealed record SetEstablishmentCodeCommand(
    Guid OrganizationId, Guid LocationId, string? Code);

/// <summary>
/// Fija el código de establecimiento fiscal de una sede — los tres primeros dígitos del número de
/// factura (FSF, bloque 0).
///
/// <para><b>Por qué es su propio comando y no un campo más del PATCH de sede.</b> Acá <c>null</c>
/// significa "borralo", y en un PATCH parcial no se distingue de "no lo mandé". Una sede que deja
/// de facturar tiene que poder quedarse sin código, y confundir las dos cosas lo borraría sin que
/// nadie lo pidiera.</para>
///
/// <para><b>Por qué lo gobierna <c>billing.supervise</c> y no <c>org.locations.manage</c>.</b> Este
/// código sale del mismo trámite del SAR que el CAI y lo registra la misma persona en la misma
/// sentada. <c>billing_supervisor</c> no administra sedes, así que pedir el permiso de sedes
/// partiría un solo trámite entre dos roles y obligaría a buscar a un administrador para escribir
/// tres dígitos.</para>
/// </summary>
public sealed class SetEstablishmentCodeHandler(
    ILocationRepository locations, ILegalEntityRepository legalEntities, TimeProvider timeProvider)
{
    public async Task<LocationDto?> HandleAsync(SetEstablishmentCodeCommand cmd, CancellationToken ct = default)
    {
        var location = await locations.GetAsync(cmd.OrganizationId, cmd.LocationId, ct);
        if (location is null)
        {
            return null;
        }

        location.SetFiscalEstablishmentCode(cmd.Code, timeProvider);
        await locations.UpdateAsync(location, ct);
        return LocationDto.FromDomain(location, await legalEntities.GetAsync(cmd.OrganizationId, location.LegalEntityId, ct));
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

        var actuales = await locations.ListAssignmentsAsync(cmd.OrganizationId, cmd.UserId, ct);
        if (actuales.All(a => a.LocationId != cmd.LocationId))
        {
            await locations.AddAssignmentAsync(
                UserLocationAssignment.Create(
                    cmd.OrganizationId, cmd.UserId, cmd.LocationId, false, cmd.AssignedBy, timeProvider),
                ct);
        }

        // La primera sede de alguien es su principal aunque no se pida: un usuario sin principal
        // no tiene por dónde empezar.
        if (cmd.MakePrimary || !actuales.Any(a => a.IsPrimary))
        {
            await locations.SetPrimaryAsync(cmd.OrganizationId, cmd.UserId, cmd.LocationId, ct);
        }

        return true;
    }
}

/// <summary>
/// Quita a un usuario de una sede. <b>Nunca la última</b> (FAC-9): un usuario sin sede no tiene
/// dónde atender ni con qué razón social facturar. Si se quita la principal, otra toma su lugar.
/// </summary>
public sealed class UnassignUserFromLocationHandler(ILocationRepository locations)
{
    public async Task HandleAsync(Guid organizationId, Guid locationId, Guid userId, CancellationToken ct = default)
    {
        var actuales = await locations.ListAssignmentsAsync(organizationId, userId, ct);
        var quitada = actuales.FirstOrDefault(a => a.LocationId == locationId);
        if (quitada is null)
        {
            return;
        }
        if (actuales.Count == 1)
        {
            throw new InvalidOperationException(
                "Es la única sede del usuario. Asignale otra antes de quitarle esta.");
        }

        await locations.RemoveAssignmentAsync(organizationId, userId, locationId, ct);
        if (quitada.IsPrimary)
        {
            var siguiente = actuales.First(a => a.LocationId != locationId);
            await locations.SetPrimaryAsync(organizationId, userId, siguiente.LocationId, ct);
        }
    }
}

/// <summary>
/// Profesionales que atienden en una sede (FAC-9). Alimenta el selector de médico de la agenda:
/// una cita solo se puede dar con un médico de la sede donde ocurre. Se lee de la base y no de
/// <c>PractitionerRole.location</c> porque la proyección a HAPI es asíncrona, y un médico recién
/// asignado tiene que aparecer en la agenda ya.
/// </summary>
public sealed class ListLocationPractitionersHandler(ILocationRepository locations)
{
    public async Task<IReadOnlyList<string>?> HandleAsync(Guid organizationId, Guid locationId, CancellationToken ct = default)
    {
        var sede = await locations.GetAsync(organizationId, locationId, ct);
        return sede is null ? null : await locations.ListPractitionerIdsAsync(organizationId, locationId, ct);
    }
}

public sealed record SetUserLocationsCommand(
    Guid OrganizationId, Guid UserId, IReadOnlyList<Guid> LocationIds, Guid PrimaryLocationId, Guid? AssignedBy);

/// <summary>
/// Fija de una vez todas las sedes de un usuario y cuál es la principal (FAC-9). Es lo que usa la
/// pantalla de equipo: editar el conjunto entero evita estados intermedios —quitar todas para
/// después agregar— que la invariante de "al menos una" no permitiría.
/// </summary>
public sealed class SetUserLocationsHandler(ILocationRepository locations)
{
    public async Task<IReadOnlyList<MyLocationDto>> HandleAsync(SetUserLocationsCommand cmd, CancellationToken ct = default)
    {
        var pedidas = cmd.LocationIds.Distinct().ToList();
        if (pedidas.Count == 0)
        {
            throw new ArgumentException("El usuario tiene que tener al menos una sede.");
        }
        if (!pedidas.Contains(cmd.PrimaryLocationId))
        {
            throw new ArgumentException("La sede principal tiene que estar entre sus sedes.");
        }

        var sedes = (await locations.ListAsync(cmd.OrganizationId, activeOnly: true, ct)).ToDictionary(l => l.Id);
        var ajenas = pedidas.Where(id => !sedes.ContainsKey(id)).ToList();
        if (ajenas.Count > 0)
        {
            throw new ArgumentException("Alguna de las sedes no existe o está inactiva.");
        }

        await locations.ReplaceAssignmentsAsync(
            cmd.OrganizationId, cmd.UserId, pedidas, cmd.PrimaryLocationId, cmd.AssignedBy, ct);

        return pedidas
            .Select(id => sedes[id])
            .OrderByDescending(l => l.Id == cmd.PrimaryLocationId)
            .ThenBy(l => l.Name)
            .Select(l => new MyLocationDto(
                l.Id, l.Code, l.Name, l.IsDefault, l.Id == cmd.PrimaryLocationId, OrganizationLocation.FhirIdFor(l.Id)))
            .ToList();
    }
}
