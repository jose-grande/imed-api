namespace IMed.Api.Domain.Billing;

/// <summary>Sobre qué se pone el tope.</summary>
public enum AuthorizationLimitKind
{
    /// <summary>Monto máximo de descuento en una venta.</summary>
    DiscountAmount = 0,

    /// <summary>Porcentaje máximo de descuento en una venta.</summary>
    DiscountPercent = 1,

    /// <summary>Monto máximo de un ajuste de inventario (U2).</summary>
    InventoryAdjustmentAmount = 2,

    /// <summary>Monto máximo de una factura que se puede anular.</summary>
    VoidAmount = 3,

    /// <summary>Diferencia máxima aceptable al cerrar un arqueo sin supervisor.</summary>
    CashCountDifference = 4,
}

/// <summary>
/// Tope de dinero de un usuario dentro de una organización (FSF §3.3, hueco G3).
///
/// <para><b>Por qué no es un rol.</b> Un rol no puede decir "este cajero descuenta hasta L 500 y
/// aquel hasta L 5,000". Crear un rol por tope multiplica los roles sin fin, y el día que cambia un
/// monto hay que tocar Keycloak. El rol dice qué clase de trabajo hace la persona; el tope dice
/// hasta dónde llega su firma, que es un dato del negocio y cambia con el tiempo.</para>
///
/// <para><b>Superar el tope no bloquea: abre el flujo de autorización.</b> Es la diferencia entre un
/// sistema que la gente usa y uno que la gente evade. Un cajero que no puede hacer el descuento
/// que el dueño ya autorizó de palabra termina llamando al supervisor para que le preste la
/// contraseña, y ahí el control desaparece sin dejar rastro. Con el flujo de autorización queda
/// registrado quién lo pidió y quién lo aprobó.</para>
///
/// <para><b>Vigencia.</b> Los topes se dan por temporada —una campaña, una ausencia del supervisor—
/// y hay que poder contestar cuál regía el día de una venta. Por eso se cierran con
/// <see cref="ValidTo"/> en vez de borrarse.</para>
/// </summary>
public sealed class UserAuthorizationLimit
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public AuthorizationLimitKind Kind { get; private set; }

    /// <summary>
    /// El tope. Monto en la moneda de la organización, o porcentaje según <see cref="Kind"/>.
    /// <c>numeric(19,4)</c> como todo el dinero del módulo.
    /// </summary>
    public decimal Value { get; private set; }

    /// <summary>
    /// Qué pasa al superarlo. <c>true</c> abre el flujo de autorización de supervisor;
    /// <c>false</c> rechaza la acción de plano.
    ///
    /// <para>El default es <c>true</c> por la razón de arriba: lo que se rechaza sin salida se
    /// evade por fuera del sistema. Rechazar de plano es para los casos donde no hay superior a
    /// quien escalar.</para>
    /// </summary>
    public bool RequiresApprovalAbove { get; private set; } = true;

    public DateOnly ValidFrom { get; private set; }

    /// <summary>Null = vigente sin fecha de fin.</summary>
    public DateOnly? ValidTo { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private UserAuthorizationLimit() { }

    public static UserAuthorizationLimit Create(
        Guid organizationId,
        Guid userId,
        AuthorizationLimitKind kind,
        decimal value,
        DateOnly validFrom,
        DateOnly? validTo = null,
        bool requiresApprovalAbove = true,
        TimeProvider? timeProvider = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "El tope no puede ser negativo.");
        }
        if (kind == AuthorizationLimitKind.DiscountPercent && value > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), "Un tope de descuento porcentual no puede pasar de 100.");
        }
        if (validTo is { } end && end < validFrom)
        {
            throw new ArgumentException(
                "La vigencia termina antes de empezar.", nameof(validTo));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new UserAuthorizationLimit
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            UserId = userId,
            Kind = kind,
            Value = value,
            RequiresApprovalAbove = requiresApprovalAbove,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>¿Regía este tope en la fecha dada? Es la pregunta que hace una auditoría.</summary>
    public bool IsInEffectOn(DateOnly day) =>
        day >= ValidFrom && (ValidTo is not { } end || day <= end);

    /// <summary>
    /// Cierra la vigencia. Se prefiere sobre borrar la fila porque las ventas ya autorizadas bajo
    /// este tope tienen que poder explicarse después.
    /// </summary>
    public void Close(DateOnly lastDay, TimeProvider? timeProvider = null)
    {
        if (lastDay < ValidFrom)
        {
            throw new ArgumentException(
                "El cierre no puede ser anterior al inicio de la vigencia.", nameof(lastDay));
        }
        ValidTo = lastDay;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
