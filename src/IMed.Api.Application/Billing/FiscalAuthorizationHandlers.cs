using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Una autorización con lo que la pantalla necesita para avisar a tiempo (B23).
/// </summary>
/// <param name="Remaining">Documentos que quedan en el rango.</param>
/// <param name="DaysUntilLimit">Días hasta la fecha límite. Negativo = ya venció.</param>
/// <param name="Alert">
/// Qué mostrar: <c>null</c> si no hay nada que avisar, o el motivo. Se calcula en el servidor y no
/// en la pantalla porque el umbral es política de la organización, no preferencia del navegador.
/// </param>
public sealed record FiscalAuthorizationDto(
    Guid Id,
    Guid LocationId,
    Guid DocumentTypeId,
    string Cai,
    string EmissionPointCode,
    long RangeFrom,
    long RangeTo,
    long NextSequence,
    long Remaining,
    DateOnly LimitDate,
    int DaysUntilLimit,
    string Status,
    string? Alert);

/// <summary>
/// Umbrales de aviso. Quedarse sin correlativos un sábado con la clínica llena es un evento real,
/// y es el que los sistemas locales suelen no prevenir: avisan cuando ya no se puede emitir.
/// </summary>
public sealed class FiscalAlertOptions
{
    public const string SectionName = "Billing:FiscalAlerts";

    /// <summary>Avisar cuando queden estos documentos o menos.</summary>
    public long RemainingThreshold { get; set; } = 100;

    /// <summary>Avisar cuando falten estos días o menos para la fecha límite.</summary>
    public int DaysThreshold { get; set; } = 30;
}

public static class FiscalAuthorizationMapper
{
    public static FiscalAuthorizationDto ToDto(
        FiscalAuthorization a, DateOnly today, FiscalAlertOptions options)
    {
        var dias = a.DaysUntilLimit(today);
        return new FiscalAuthorizationDto(
            a.Id, a.LocationId, a.DocumentTypeId, a.Cai, a.EmissionPointCode,
            a.RangeFrom, a.RangeTo, a.NextSequence, a.Remaining,
            a.LimitDate, dias, a.Status.ToString(),
            Alert: BuildAlert(a, today, dias, options));
    }

    /// <summary>
    /// El aviso más urgente, o null. Se devuelve uno solo y no una lista: dos avisos compitiendo
    /// en la misma fila hacen que no se lea ninguno.
    /// </summary>
    private static string? BuildAlert(
        FiscalAuthorization a, DateOnly today, int dias, FiscalAlertOptions options)
    {
        // Lo que ya no sirve va primero: no tiene sentido avisar "quedan 80 documentos" de una
        // autorización vencida.
        return a.BlockedReason(today) switch
        {
            FiscalReservationFailure.Revoked => "Dada de baja: no emite.",
            FiscalReservationFailure.Expired => $"Vencida el {a.LimitDate:dd/MM/yyyy}: no emite.",
            FiscalReservationFailure.Exhausted => "Sin correlativos: no emite.",
            _ when a.Remaining <= options.RemainingThreshold =>
                $"Quedan {a.Remaining} documento(s).",
            _ when dias <= options.DaysThreshold =>
                $"Faltan {dias} día(s) para la fecha límite.",
            _ => null,
        };
    }
}

public sealed class ListFiscalAuthorizationsHandler(
    IFiscalRepository repo, IClinicClock clock, FiscalAlertOptions options)
{
    public async Task<IReadOnlyList<FiscalAuthorizationDto>> HandleAsync(
        Guid orgId, CancellationToken ct = default)
    {
        var today = clock.Today();
        var rows = await repo.ListAuthorizationsAsync(orgId, ct);
        return rows.Select(a => FiscalAuthorizationMapper.ToDto(a, today, options)).ToList();
    }
}

public sealed record RegisterFiscalAuthorizationCommand(
    Guid OrganizationId,
    Guid LocationId,
    Guid DocumentTypeId,
    string Cai,
    string EmissionPointCode,
    long RangeFrom,
    long RangeTo,
    DateOnly LimitDate);

public sealed class RegisterFiscalAuthorizationHandler(
    IFiscalRepository repo, IClinicClock clock, FiscalAlertOptions options)
{
    public async Task<FiscalAuthorizationDto> HandleAsync(
        RegisterFiscalAuthorizationCommand cmd, CancellationToken ct = default)
    {
        var tipo = await repo.GetDocumentTypeAsync(cmd.OrganizationId, cmd.DocumentTypeId, ct)
            ?? throw new ArgumentException(
                "El tipo de documento no existe en esta organización.", nameof(cmd));

        // Una autorización que nace vencida es un error de captura —el CAI llegó tarde o se
        // tecleó mal la fecha— y descubrirlo al intentar cobrar es lo peor que puede pasar.
        var today = clock.Today();
        if (cmd.LimitDate < today)
        {
            throw new ArgumentException(
                $"La fecha límite ya pasó ({cmd.LimitDate:dd/MM/yyyy}): la autorización no serviría para emitir.",
                nameof(cmd));
        }

        var auth = FiscalAuthorization.Create(
            cmd.OrganizationId, cmd.LocationId, tipo.Id, cmd.Cai, cmd.EmissionPointCode,
            cmd.RangeFrom, cmd.RangeTo, cmd.LimitDate);

        await repo.AddAuthorizationAsync(auth, ct);
        return FiscalAuthorizationMapper.ToDto(auth, today, options);
    }
}

public sealed class RevokeFiscalAuthorizationHandler(
    IFiscalRepository repo, IClinicClock clock, FiscalAlertOptions options)
{
    public async Task<FiscalAuthorizationDto?> HandleAsync(
        Guid orgId, Guid authorizationId, CancellationToken ct = default)
    {
        var auth = await repo.GetAuthorizationAsync(orgId, authorizationId, ct);
        if (auth is null)
        {
            return null;
        }

        auth.Revoke();
        await repo.UpdateAuthorizationAsync(auth, ct);
        return FiscalAuthorizationMapper.ToDto(auth, clock.Today(), options);
    }
}
