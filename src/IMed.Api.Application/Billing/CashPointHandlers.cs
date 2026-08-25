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
    IReadOnlyList<string> EmissionPointSharedWith);

public static class CashPointMapper
{
    public static CashPointDto ToDto(CashPoint c, IReadOnlyList<CashPoint> todas) =>
        new(c.Id, c.LocationId, c.Code, c.Name, c.Profile.ToString(), c.EmissionPointCode,
            c.IsTemporary, c.PrinterProfile, c.Active,
            todas
                .Where(o => o.Id != c.Id
                            && o.Active
                            && o.LocationId == c.LocationId
                            && o.EmissionPointCode == c.EmissionPointCode)
                .Select(o => o.Name)
                .Order()
                .ToList());
}

public sealed class ListCashPointsHandler(IFiscalRepository repo)
{
    public async Task<IReadOnlyList<CashPointDto>> HandleAsync(Guid orgId, CancellationToken ct = default)
    {
        var todas = await repo.ListCashPointsAsync(orgId, ct);
        return todas.Select(c => CashPointMapper.ToDto(c, todas)).ToList();
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

public sealed class SetCashPointActiveHandler(IFiscalRepository repo)
{
    public async Task<CashPointDto?> HandleAsync(
        Guid orgId, Guid cashPointId, bool active, CancellationToken ct = default)
    {
        var caja = await repo.GetCashPointAsync(orgId, cashPointId, ct);
        if (caja is null)
        {
            return null;
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
        return CashPointMapper.ToDto(caja, todas);
    }
}
