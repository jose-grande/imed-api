using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Cash;

namespace IMed.Api.Application.Billing;

public sealed record CashSessionDto(
    Guid Id,
    Guid CashPointId,
    Guid CashierUserId,
    decimal OpeningAmount,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal? CountedAmount,
    decimal? ExpectedAmount,
    decimal? Difference,
    string? DifferenceNote,
    Guid? DifferenceAuthorizedBy,
    string Status,
    Guid? PreviousSessionId);

/// <summary>
/// Lo que la estación de caja necesita ver del turno abierto <b>mientras está abierto</b>.
///
/// <para>🔴 <b>No incluye el efectivo esperado, a propósito.</b> Si el número está a la vista, lo
/// que el cajero registra al arquear es ese número y no lo que hay en la gaveta — y el arqueo deja
/// de servir para lo único que sirve. Alguien va a pedir mostrarlo "para agilizar"; la respuesta
/// es que un arqueo que se agiliza así no es un arqueo.</para>
/// </summary>
public sealed record OpenSessionSummaryDto(
    CashSessionDto Session,
    int PaymentCount,
    int MovementCount);

public sealed record CashMovementDto(
    Guid Id,
    string Kind,
    decimal Amount,
    string Reason,
    Guid PerformedBy,
    Guid? AuthorizedBy,
    DateTimeOffset OccurredAt);

public static class CashMapper
{
    public static CashSessionDto ToDto(CashSession s) =>
        new(s.Id, s.CashPointId, s.CashierUserId, s.OpeningAmount, s.OpenedAt, s.ClosedAt,
            s.CountedAmount, s.ExpectedAmount, s.Difference, s.DifferenceNote,
            s.DifferenceAuthorizedBy, s.Status.ToString(), s.PreviousSessionId);

    public static CashMovementDto ToDto(CashMovement m) =>
        new(m.Id, m.Kind.ToString(), m.Amount, m.Reason, m.PerformedBy, m.AuthorizedBy, m.OccurredAt);
}

public sealed record CashResult(CashSessionDto? Session, string? Code, string? Message)
{
    public bool Succeeded => Session is not null;
    public static CashResult Ok(CashSessionDto s) => new(s, null, null);
    public static CashResult Reject(string code, string message) => new(null, code, message);
}

/// <summary>
/// Abre un turno de caja.
///
/// <para>Con <c>previousSessionId</c> es un <b>traspaso</b>: cierra el turno del cajero saliente y
/// abre el del entrante sobre la misma caja. Obligar a cerrar la caja física en cada cambio de
/// guardia haría que el entrante arranque contando lo que el saliente ya contó.</para>
/// </summary>
public sealed class OpenCashSessionHandler(ICashRepository cash, IFiscalRepository fiscal)
{
    public async Task<CashResult> HandleAsync(
        Guid orgId, Guid cashPointId, Guid cashierUserId, decimal openingAmount,
        CancellationToken ct = default)
    {
        var caja = await fiscal.GetCashPointAsync(orgId, cashPointId, ct);
        if (caja is null)
        {
            return CashResult.Reject("caja_inexistente", "La caja no existe.");
        }
        if (!caja.Active)
        {
            return CashResult.Reject("caja_cerrada", $"La caja '{caja.Name}' está cerrada.");
        }

        // Dos turnos simultáneos sobre la misma gaveta hacen que el arqueo de cada uno reclame el
        // efectivo del otro. Es un traspaso lo que corresponde, no un turno paralelo.
        if (await cash.FindOpenSessionByPointAsync(orgId, cashPointId, ct) is { } abierto)
        {
            return CashResult.Reject(
                "turno_abierto",
                $"La caja '{caja.Name}' ya tiene un turno abierto. Cerralo o hacé un traspaso.");
        }

        // Un cajero con dos turnos abiertos en cajas distintas no puede estar en los dos lugares:
        // lo más probable es que se olvidó de cerrar el anterior.
        if (await cash.FindOpenSessionByCashierAsync(orgId, cashierUserId, ct) is { } suyo)
        {
            return CashResult.Reject(
                "cajero_con_turno",
                "Ya tenés un turno abierto en otra caja. Cerralo antes de abrir otro.");
        }

        var turno = CashSession.Open(orgId, cashPointId, cashierUserId, openingAmount);
        await cash.AddSessionAsync(turno, ct);
        return CashResult.Ok(CashMapper.ToDto(turno));
    }
}

/// <summary>
/// Calcula cuánto efectivo debería haber en la gaveta.
///
/// <para>Va aparte del cierre porque es la pieza que el arqueo a ciegas <b>no</b> puede exponer
/// antes de tiempo: existe como servicio interno, sin endpoint que lo devuelva durante el turno.</para>
/// </summary>
public sealed class CashExpectationCalculator(ICashRepository cash)
{
    public async Task<decimal> ExpectedCashAsync(
        Guid orgId, CashSession session, CancellationToken ct = default)
    {
        var pagos = await cash.ListPaymentsAsync(orgId, session.Id, ct);
        var movimientos = await cash.ListMovementsAsync(orgId, session.Id, ct);

        // Solo el efectivo cuenta, y neto de vuelto: si el paciente pagó 500 por una consulta de
        // 380, en la gaveta quedan 380. Tarjeta y transferencia no tocan la caja física.
        var cobrado = pagos.Sum(p => p.CashImpact);
        var movido = movimientos.Sum(m => m.SignedAmount);

        return Math.Round(session.OpeningAmount + cobrado + movido, 2, MidpointRounding.AwayFromZero);
    }
}

public sealed record CloseCashSessionCommand(
    Guid OrganizationId,
    Guid SessionId,
    Guid ClosedByUserId,
    decimal CountedAmount,
    string? Note,
    Guid? AuthorizedBy);

/// <summary>
/// Cierra el turno con lo que el cajero contó.
///
/// <para>El esperado se calcula <b>en este momento</b> y recién acá se guarda: durante el turno no
/// existe en ninguna respuesta del servidor.</para>
/// </summary>
public sealed class CloseCashSessionHandler(
    ICashRepository cash,
    CashExpectationCalculator calculator,
    IAuthorizationLimitRepository limits,
    IClinicClock clock)
{
    public async Task<CashResult> HandleAsync(CloseCashSessionCommand cmd, CancellationToken ct = default)
    {
        var turno = await cash.GetSessionForUpdateAsync(cmd.OrganizationId, cmd.SessionId, ct);
        if (turno is null)
        {
            return CashResult.Reject("turno_inexistente", "El turno no existe.");
        }
        if (!turno.IsOpen)
        {
            return CashResult.Reject("turno_cerrado", "El turno ya está cerrado.");
        }

        var esperado = await calculator.ExpectedCashAsync(cmd.OrganizationId, turno, ct);
        var diferencia = cmd.CountedAmount - esperado;

        if (diferencia != 0 && string.IsNullOrWhiteSpace(cmd.Note))
        {
            return CashResult.Reject(
                "diferencia_sin_explicar",
                "Hay diferencia de arqueo y falta explicarla.");
        }

        // Una diferencia grande sin autorización es un faltante que se cierra solo. El tope lo
        // define la organización; sin tope configurado no se exige autorización.
        if (diferencia != 0 && cmd.AuthorizedBy is null)
        {
            var tope = await limits.FindEffectiveAsync(
                cmd.OrganizationId, turno.CashierUserId,
                AuthorizationLimitKind.CashCountDifference, clock.Today(), ct);

            if (tope is not null && Math.Abs(diferencia) > tope.Value)
            {
                return CashResult.Reject(
                    "diferencia_sobre_tope",
                    $"La diferencia de L {Math.Abs(diferencia):N2} supera el tope de "
                    + $"L {tope.Value:N2}: requiere autorización de un supervisor.");
            }
        }

        try
        {
            turno.Close(cmd.CountedAmount, esperado, cmd.Note, cmd.AuthorizedBy);
        }
        catch (ArgumentException ex)
        {
            return CashResult.Reject("solicitud_invalida", ex.Message);
        }

        await cash.UpdateSessionAsync(turno, ct);
        return CashResult.Ok(CashMapper.ToDto(turno));
    }
}

/// <summary>
/// Traspaso de cajero: cierra el turno del saliente y abre el del entrante sobre la misma caja,
/// sin que la gaveta se cuente dos veces.
/// </summary>
public sealed record HandoverCommand(
    Guid OrganizationId,
    Guid SessionId,
    Guid OutgoingUserId,
    Guid IncomingUserId,
    decimal CountedAmount,
    string? Note,
    Guid? AuthorizedBy);

public sealed record HandoverResult(CashSessionDto? Closed, CashSessionDto? Opened, string? Code, string? Message)
{
    public bool Succeeded => Opened is not null;
}

public sealed class HandoverCashSessionHandler(
    ICashRepository cash, CloseCashSessionHandler close)
{
    public async Task<HandoverResult> HandleAsync(HandoverCommand cmd, CancellationToken ct = default)
    {
        var turno = await cash.GetSessionAsync(cmd.OrganizationId, cmd.SessionId, ct);
        if (turno is null)
        {
            return new HandoverResult(null, null, "turno_inexistente", "El turno no existe.");
        }

        var cerrado = await close.HandleAsync(new CloseCashSessionCommand(
            cmd.OrganizationId, cmd.SessionId, cmd.OutgoingUserId,
            cmd.CountedAmount, cmd.Note, cmd.AuthorizedBy), ct);

        if (!cerrado.Succeeded)
        {
            return new HandoverResult(null, null, cerrado.Code, cerrado.Message);
        }

        // El entrante arranca con lo que el saliente dejó contado: contar de nuevo la misma
        // gaveta es trabajo duplicado que además puede dar dos números distintos.
        var nuevo = CashSession.Open(
            cmd.OrganizationId, turno.CashPointId, cmd.IncomingUserId,
            cmd.CountedAmount, previousSessionId: turno.Id);

        await cash.AddSessionAsync(nuevo, ct);
        return new HandoverResult(cerrado.Session, CashMapper.ToDto(nuevo), null, null);
    }
}

public sealed record RegisterMovementCommand(
    Guid OrganizationId,
    Guid SessionId,
    string Kind,
    decimal Amount,
    string Reason,
    Guid PerformedBy,
    Guid? AuthorizedBy);

/// <summary>
/// Registra un retiro o un ingreso de efectivo. Sin esto, retirar dinero de la caja produce un
/// faltante de arqueo que nadie puede explicar — y una caja que no puede retirar acumula
/// L 200,000 esperando el cierre.
/// </summary>
public sealed class RegisterCashMovementHandler(ICashRepository cash)
{
    public async Task<CashMovementDto?> HandleAsync(
        RegisterMovementCommand cmd, CancellationToken ct = default)
    {
        var turno = await cash.GetSessionAsync(cmd.OrganizationId, cmd.SessionId, ct);
        if (turno is null || !turno.IsOpen)
        {
            return null;
        }

        var movimiento = CashMovement.Create(
            cmd.OrganizationId, cmd.SessionId,
            Enum.TryParse<CashMovementKind>(cmd.Kind, ignoreCase: true, out var k)
                ? k : CashMovementKind.Adjustment,
            cmd.Amount, cmd.Reason, cmd.PerformedBy, cmd.AuthorizedBy);

        await cash.AddMovementAsync(movimiento, ct);
        return CashMapper.ToDto(movimiento);
    }
}

/// <summary>Estado del turno abierto de un cajero, sin revelar el esperado.</summary>
public sealed class GetOpenSessionHandler(ICashRepository cash)
{
    public async Task<OpenSessionSummaryDto?> HandleAsync(
        Guid orgId, Guid cashierUserId, CancellationToken ct = default)
    {
        var turno = await cash.FindOpenSessionByCashierAsync(orgId, cashierUserId, ct);
        if (turno is null)
        {
            return null;
        }

        var pagos = await cash.ListPaymentsAsync(orgId, turno.Id, ct);
        var movimientos = await cash.ListMovementsAsync(orgId, turno.Id, ct);

        // Deliberadamente NO se calcula el esperado acá. `CashExpectationCalculator` existe y
        // está inyectable, pero llamarlo en esta respuesta convierte el arqueo en una
        // transcripción. Si alguien viene a agregarlo "para agilizar", la prueba
        // `ElArqueo_noRevelaElEsperadoAntesDelConteo` lo detiene.
        return new OpenSessionSummaryDto(
            CashMapper.ToDto(turno), pagos.Count, movimientos.Count);
    }
}
