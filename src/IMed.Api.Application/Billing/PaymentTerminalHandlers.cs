using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Cash;

namespace IMed.Api.Application.Billing;

public sealed record PaymentTerminalDto(
    Guid Id, Guid LocationId, string Name, string Bank, string Kind, string Method, bool Active);

public sealed record CreatePaymentTerminalCommand(
    Guid OrganizationId, Guid LocationId, string Name, string Bank, string Kind);

public static class PaymentTerminalMapper
{
    public static PaymentTerminalDto ToDto(PaymentTerminal t) =>
        new(t.Id, t.LocationId, t.Name, t.Bank, t.Kind.ToString(), t.Receives.ToString(), t.Active);

    /// <summary>
    /// Cómo sale en el cierre: "POS BAC (BAC Credomatic)". Si el nombre ya dice el banco
    /// ("POS Ficohsa"), no se repite.
    /// </summary>
    public static string Label(PaymentTerminal t) =>
        t.Name.Contains(t.Bank, StringComparison.CurrentCultureIgnoreCase) ? t.Name : $"{t.Name} ({t.Bank})";
}

/// <summary>Alta, baja y lista de los POS y cuentas bancarias de las sedes.</summary>
public sealed class PaymentTerminalHandlers(
    IPaymentTerminalRepository terminals,
    ILocationRepository locations,
    ICashRepository cash,
    IFiscalRepository fiscal)
{
    public async Task<IReadOnlyList<PaymentTerminalDto>> ListAsync(
        Guid orgId, Guid? locationId, CancellationToken ct = default) =>
        (await terminals.ListAsync(orgId, locationId, ct)).Select(PaymentTerminalMapper.ToDto).ToList();

    /// <summary>
    /// Los activos de la sede de la caja donde el usuario tiene el turno abierto: lo que el cajero
    /// puede elegir al cobrar. Sin turno abierto, vacío.
    /// </summary>
    public async Task<IReadOnlyList<PaymentTerminalDto>> ForMySessionAsync(
        Guid orgId, Guid userId, CancellationToken ct = default)
    {
        var turno = await cash.FindOpenSessionByCashierAsync(orgId, userId, ct);
        if (turno is null)
        {
            return [];
        }
        var caja = await fiscal.GetCashPointAsync(orgId, turno.CashPointId, ct);
        if (caja is null)
        {
            return [];
        }
        return (await terminals.ListAsync(orgId, caja.LocationId, ct))
            .Where(t => t.Active)
            .Select(PaymentTerminalMapper.ToDto)
            .ToList();
    }

    /// <exception cref="ArgumentException">Datos incompletos, tipo desconocido o sede inexistente.</exception>
    public async Task<PaymentTerminalDto> CreateAsync(CreatePaymentTerminalCommand cmd, CancellationToken ct = default)
    {
        if (!Enum.TryParse<PaymentTerminalKind>(cmd.Kind, ignoreCase: true, out var tipo) || !Enum.IsDefined(tipo))
        {
            throw new ArgumentException("El tipo es PosTerminal o BankAccount.", nameof(cmd));
        }
        if (await locations.GetAsync(cmd.OrganizationId, cmd.LocationId, ct) is null)
        {
            throw new ArgumentException("La sede no existe.", nameof(cmd));
        }
        var terminal = PaymentTerminal.Create(cmd.OrganizationId, cmd.LocationId, cmd.Name, cmd.Bank, tipo);
        var repetido = (await terminals.ListAsync(cmd.OrganizationId, cmd.LocationId, ct))
            .Any(t => t.Kind == tipo && string.Equals(t.Name, terminal.Name, StringComparison.OrdinalIgnoreCase));
        if (repetido)
        {
            // Dos "POS BAC" en la misma sede no se distinguen en el cierre: es justo lo que esto evita.
            throw new ArgumentException($"Ya hay uno que se llama «{terminal.Name}» en esa sede.", nameof(cmd));
        }
        await terminals.AddAsync(terminal, ct);
        return PaymentTerminalMapper.ToDto(terminal);
    }

    public async Task<PaymentTerminalDto?> SetActiveAsync(
        Guid orgId, Guid terminalId, bool active, CancellationToken ct = default)
    {
        var t = await terminals.GetAsync(orgId, terminalId, ct);
        if (t is null)
        {
            return null;
        }
        t.SetActive(active);
        await terminals.UpdateAsync(t, ct);
        return PaymentTerminalMapper.ToDto(t);
    }
}

/// <summary>
/// La regla del cobro: si la sede tiene POS (o cuentas) activos, un pago con tarjeta (o una
/// transferencia) tiene que decir a cuál entró. Sin ninguno dado de alta, todo sigue como antes:
/// una clínica con un solo POS no tiene por qué configurar nada.
/// </summary>
public static class PaymentTerminalRule
{
    /// <returns><c>(código, mensaje)</c> del rechazo, o <c>null</c> si el renglón es válido.</returns>
    public static (string Code, string Message)? Check(
        PaymentMethod method, Guid? terminalId, IReadOnlyList<PaymentTerminal> activosDeLaSede)
    {
        var tipo = PaymentTerminal.KindFor(method);
        if (terminalId is { } id)
        {
            if (tipo is null)
            {
                return ("terminal_no_aplica", "Solo la tarjeta va a un POS y la transferencia a una cuenta bancaria.");
            }
            var t = activosDeLaSede.FirstOrDefault(x => x.Id == id);
            if (t is null)
            {
                return ("terminal_invalido", "Ese POS o cuenta no existe, está inactivo o es de otra sede.");
            }
            if (t.Kind != tipo)
            {
                return ("terminal_invalido", "Un pago con tarjeta va a un POS y una transferencia a una cuenta bancaria.");
            }
            return null;
        }
        if (tipo is { } k && activosDeLaSede.Any(x => x.Kind == k))
        {
            return k == PaymentTerminalKind.PosTerminal
                ? ("falta_terminal", "Elegí en qué POS se pasó la tarjeta: el cierre se cuadra por banco.")
                : ("falta_terminal", "Elegí a qué cuenta entró la transferencia: el cierre se cuadra por banco.");
        }
        return null;
    }
}
