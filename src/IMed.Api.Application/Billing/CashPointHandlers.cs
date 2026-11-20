using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Una caja con su estrategia de numeración explicada (B24).
/// </summary>
/// <param name="EmissionPointSharedWith">
/// Otras cajas que numeran con el mismo punto de emisión. La pantalla lo dice en palabras —
/// <i>"Caja 3 emitirá con el punto 002, compartido con Caja 1 y 2"</i>— porque compartir punto
/// significa compartir la secuencia, y quien da de alta una caja tiene que saberlo antes de
/// guardar, no cuando el correlativo salte.
/// </param>
/// <param name="Active">
/// Habilitada: se puede abrir turno en ella. <b>No</b> quiere decir que esté operando — eso lo dice
/// <paramref name="OpenShift"/>. Mezclar las dos cosas bajo "abierta/cerrada" hacía creer que dar
/// de alta una caja la dejaba cobrando, y que deshabilitarla era hacer el corte del día.
/// </param>
/// <param name="OpenShift">El turno abierto en esta caja ahora mismo, si hay uno.</param>
public sealed record CashPointDto(
    Guid Id,
    Guid LocationId,
    string Code,
    string Name,
    string Profile,
    string EmissionPointCode,
    bool IsTemporary,
    string? PrinterProfile,
    bool Active,
    IReadOnlyList<string> EmissionPointSharedWith,
    CashPointShiftDto? OpenShift = null);

/// <summary>Quién tiene la caja abierta y desde cuándo.</summary>
public sealed record CashPointShiftDto(
    Guid SessionId,
    Guid CashierUserId,
    string? CashierName,
    DateTimeOffset OpenedAt);

public static class CashPointMapper
{
    public static CashPointDto ToDto(
        CashPoint c, IReadOnlyList<CashPoint> todas, CashPointShiftDto? turno = null) =>
        new(c.Id, c.LocationId, c.Code, c.Name, c.Profile.ToString(), c.EmissionPointCode,
            c.IsTemporary, c.PrinterProfile, c.Active,
            todas
                .Where(o => o.Id != c.Id
                            && o.Active
                            && o.LocationId == c.LocationId
                            && o.EmissionPointCode == c.EmissionPointCode)
                .Select(o => o.Name)
                .Order()
                .ToList(),
            turno);

    /// <summary>
    /// Turnos abiertos por caja, con el nombre del cajero. El nombre sale de la membresía (se
    /// registra al iniciar sesión); si no está, la pantalla muestra el turno sin nombre antes que
    /// esconderlo.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, CashPointShiftDto>> OpenShiftsAsync(
        ICashRepository cash, IOrganizationMemberRepository members, Guid orgId, CancellationToken ct)
    {
        var turnos = await cash.ListOpenSessionsAsync(orgId, ct);
        if (turnos.Count == 0)
        {
            return new Dictionary<Guid, CashPointShiftDto>();
        }
        var nombres = (await members.ListByOrganizationAsync(orgId, ct))
            .GroupBy(m => m.KeycloakUserId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName ?? g.First().Email);
        return turnos
            .GroupBy(t => t.CashPointId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var t = g.OrderByDescending(x => x.OpenedAt).First();
                    return new CashPointShiftDto(
                        t.Id, t.CashierUserId, nombres.GetValueOrDefault(t.CashierUserId), t.OpenedAt);
                });
    }
}

/// <summary>
/// Cajas visibles para el usuario. Quien administra la facturación (<c>billing.supervise</c>) ve
/// todas; los demás, solo las de las sedes que tienen asignadas: una caja de otra sucursal no es
/// asunto suyo, y abrir turno en ella dejaba cobrar a nombre de otra razón social.
/// </summary>
public sealed class ListCashPointsHandler(
    IFiscalRepository repo,
    ILocationRepository locations,
    ILocationProvisioning provisioning,
    ICashRepository cash,
    IOrganizationMemberRepository members)
{
    public async Task<IReadOnlyList<CashPointDto>> HandleAsync(
        Guid orgId, Guid? userId, bool allLocations, CancellationToken ct = default)
    {
        var todas = await repo.ListCashPointsAsync(orgId, ct);
        IEnumerable<CashPoint> visibles = todas;
        if (!allLocations)
        {
            var sedes = userId is { } usuario
                ? await AssignedLocationsAsync(locations, provisioning, orgId, usuario, ct)
                : new HashSet<Guid>();
            visibles = todas.Where(c => sedes.Contains(c.LocationId));
        }
        // Con quién comparte punto de emisión se calcula sobre todas: es un dato fiscal de la caja.
        var turnos = await CashPointMapper.OpenShiftsAsync(cash, members, orgId, ct);
        return visibles
            .Select(c => CashPointMapper.ToDto(c, todas, turnos.GetValueOrDefault(c.Id)))
            .ToList();
    }

    /// <summary>Sedes asignadas al usuario. Todo usuario tiene al menos una (FAC-9).</summary>
    internal static async Task<IReadOnlySet<Guid>> AssignedLocationsAsync(
        ILocationRepository locations, ILocationProvisioning provisioning, Guid orgId, Guid userId, CancellationToken ct)
    {
        var asignadas = await locations.ListForUserAsync(orgId, userId, ct);
        if (asignadas.Count == 0 && await provisioning.EnsureUserAssignedAsync(orgId, userId, null, ct))
        {
            asignadas = await locations.ListForUserAsync(orgId, userId, ct);
        }
        return asignadas.Select(a => a.Location.Id).ToHashSet();
    }
}

public sealed record CreateCashPointCommand(
    Guid OrganizationId,
    Guid LocationId,
    string Code,
    string Name,
    string EmissionPointCode,
    string? Profile,
    bool IsTemporary,
    string? PrinterProfile);

/// <summary>
/// Da de alta una caja. <b>En caliente</b>: una clínica abre una caja extra el día que hay fila,
/// no la semana anterior. Si esto exigiera un trámite previo o reiniciar algo, la caja extra se
/// abre igual pero por fuera del sistema, con recibos a mano.
/// </summary>
public sealed class CreateCashPointHandler(IFiscalRepository repo)
{
    public async Task<CashPointDto> HandleAsync(CreateCashPointCommand cmd, CancellationToken ct = default)
    {
        if (await repo.CashPointCodeExistsAsync(cmd.OrganizationId, cmd.Code, ct))
        {
            throw new InvalidOperationException($"Ya existe una caja con el código '{cmd.Code}'.");
        }

        var profile = ParseProfile(cmd.Profile);
        var caja = CashPoint.Create(
            cmd.OrganizationId, cmd.LocationId, cmd.Code, cmd.Name, cmd.EmissionPointCode,
            profile, cmd.IsTemporary, cmd.PrinterProfile);

        await repo.AddCashPointAsync(caja, ct);

        var todas = await repo.ListCashPointsAsync(cmd.OrganizationId, ct);
        return CashPointMapper.ToDto(caja, todas);
    }

    private static CashPointProfile ParseProfile(string? profile) =>
        Enum.TryParse<CashPointProfile>(profile, ignoreCase: true, out var p)
            ? p
            : CashPointProfile.Services;
}

/// <summary>
/// Habilita o deshabilita una caja. Deshabilitar no es el corte del día: es dar la caja de baja.
/// Por eso no se permite con un turno abierto — el turno quedaría sin arqueo, con efectivo que nadie
/// contó, en una caja que ya no aparece en ninguna lista.
/// </summary>
public sealed class SetCashPointActiveHandler(
    IFiscalRepository repo, ICashRepository cash, IOrganizationMemberRepository members)
{
    /// <exception cref="InvalidOperationException">La caja tiene un turno abierto.</exception>
    public async Task<CashPointDto?> HandleAsync(
        Guid orgId, Guid cashPointId, bool active, CancellationToken ct = default)
    {
        var caja = await repo.GetCashPointAsync(orgId, cashPointId, ct);
        if (caja is null)
        {
            return null;
        }

        if (!active && await cash.FindOpenSessionByPointAsync(orgId, cashPointId, ct) is not null)
        {
            throw new InvalidOperationException(
                $"La caja '{caja.Name}' tiene un turno abierto. Cerralo con su arqueo en Caja antes de deshabilitarla.");
        }

        if (active)
        {
            caja.Reactivate();
        }
        else
        {
            caja.Deactivate();
        }
        await repo.UpdateCashPointAsync(caja, ct);

        var todas = await repo.ListCashPointsAsync(orgId, ct);
        var turnos = await CashPointMapper.OpenShiftsAsync(cash, members, orgId, ct);
        return CashPointMapper.ToDto(caja, todas, turnos.GetValueOrDefault(caja.Id));
    }
}
