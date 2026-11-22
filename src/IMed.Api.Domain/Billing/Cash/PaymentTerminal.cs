namespace IMed.Api.Domain.Billing.Cash;

/// <summary>Qué recibe el dinero que no es efectivo.</summary>
public enum PaymentTerminalKind
{
    /// <summary>Un POS de un banco. Recibe los pagos con tarjeta.</summary>
    PosTerminal = 0,

    /// <summary>Una cuenta bancaria. Recibe las transferencias.</summary>
    BankAccount = 1,
}

/// <summary>
/// Un POS o una cuenta bancaria de una sede: a dónde entró un pago con tarjeta o una transferencia.
///
/// <para><b>Por qué existe.</b> Una clínica tiene POS de varios bancos, y cada banco liquida su
/// cierre de lote por separado. Sin saber de qué POS salió cada voucher, el cierre del turno da un
/// solo total de tarjeta que no se puede comparar con ningún lote. Escribir el banco en la
/// referencia no alcanza: "BAC", "bac" y "Bac credomatic" no suman juntos.</para>
///
/// <para><b>De la sede, no de la caja.</b> Un POS lo comparten dos cajas, y una caja puede tener
/// dos POS. El cajero elige al cobrar entre los de la sede de su caja.</para>
/// </summary>
public sealed class PaymentTerminal
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid LocationId { get; private set; }

    /// <summary>Cómo lo reconoce el cajero, por ejemplo "POS BAC mostrador".</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>El banco que liquida, para el reporte y la conciliación.</summary>
    public string Bank { get; private set; } = string.Empty;

    public PaymentTerminalKind Kind { get; private set; }
    public bool Active { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private PaymentTerminal() { }

    public static PaymentTerminal Create(
        Guid organizationId, Guid locationId, string name, string bank, PaymentTerminalKind kind,
        TimeProvider? timeProvider = null)
    {
        var nombre = Requerido(name, nameof(name), "el nombre");
        var banco = Requerido(bank, nameof(bank), "el banco");
        if (nombre.Length > 100)
        {
            throw new ArgumentException("El nombre va hasta 100 caracteres.", nameof(name));
        }
        if (banco.Length > 100)
        {
            throw new ArgumentException("El banco va hasta 100 caracteres.", nameof(bank));
        }
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("El tipo es POS o cuenta bancaria.", nameof(kind));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new PaymentTerminal
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LocationId = locationId,
            Name = nombre,
            Bank = banco,
            Kind = kind,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Se desactiva, no se borra: los pagos que ya recibió lo siguen nombrando en los cierres.
    /// </summary>
    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>El método de pago que recibe este terminal.</summary>
    public PaymentMethod Receives => Kind == PaymentTerminalKind.PosTerminal ? PaymentMethod.Card : PaymentMethod.Transfer;

    /// <summary>El tipo de terminal que corresponde a un método, o <c>null</c> si el método no usa terminal.</summary>
    public static PaymentTerminalKind? KindFor(PaymentMethod method) => method switch
    {
        PaymentMethod.Card => PaymentTerminalKind.PosTerminal,
        PaymentMethod.Transfer => PaymentTerminalKind.BankAccount,
        _ => null,
    };

    private static string Requerido(string? value, string param, string que) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Falta {que}.", param)
            : value.Trim();
}
