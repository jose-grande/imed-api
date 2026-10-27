namespace IMed.Api.Domain.Inventory;

/// <summary>
/// Bodega: un lugar físico de una sede donde hay existencias (FSF §8, P7).
///
/// <para><b>De una sede, no de la organización.</b> El inventario es operativo (ADR-008): lo que hay
/// en el estante de la sede norte no se puede despachar en la sede sur. Por eso toda bodega cuelga
/// de una sede, y la farmacia, el laboratorio y un consultorio son bodegas, no conceptos aparte:
/// mover entre ellos es un traslado y no un mecanismo nuevo.</para>
///
/// <para><b>Cada sede trae su bodega general</b> (P7), creada sola, para que una sede nueva pueda
/// recibir mercadería el primer día sin que nadie tenga que configurar nada.</para>
/// </summary>
public sealed class Warehouse
{
    public const string GeneralCode = "GENERAL";

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Sede donde está (<c>organization_location</c>).</summary>
    public Guid LocationId { get; private set; }

    /// <summary>Código corto, único en la organización.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public WarehouseKind Kind { get; private set; }

    /// <summary>
    /// Es la bodega general automática de la sede. Hay exactamente una por sede: el índice único
    /// filtrado lo garantiza aunque dos procesos la creen a la vez.
    /// </summary>
    public bool IsGeneral { get; private set; }

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Warehouse() { }

    public static Warehouse Create(
        Guid organizationId, Guid locationId, string code, string name, WarehouseKind kind,
        TimeProvider? timeProvider = null)
    {
        if (locationId == Guid.Empty)
        {
            throw new ArgumentException("La bodega necesita su sede.", nameof(locationId));
        }
        if (kind == WarehouseKind.General)
        {
            throw new ArgumentException(
                "La bodega general la crea el sistema con cada sede; las demás son de farmacia, laboratorio, consultorio u otra.",
                nameof(kind));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var w = new Warehouse
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LocationId = locationId,
            Kind = kind,
            CreatedAt = now,
            UpdatedAt = now,
        };
        w.SetCode(code);
        w.Rename(name, now);
        return w;
    }

    /// <summary>La bodega general de una sede (P7). Su código lleva el de la sede para ser único.</summary>
    public static Warehouse CreateGeneral(
        Guid organizationId, Guid locationId, string locationCode, string locationName,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var w = new Warehouse
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LocationId = locationId,
            Kind = WarehouseKind.General,
            IsGeneral = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var codigo = $"{GeneralCode}-{locationCode}".ToUpperInvariant();
        w.Code = codigo.Length > 40 ? codigo[..40] : codigo;
        w.Name = $"Bodega general — {locationName}";
        return w;
    }

    public void Update(string name, WarehouseKind kind, bool active, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (IsGeneral)
        {
            // La general sigue siendo general y no se apaga: es el destino por defecto de la sede.
            if (kind != WarehouseKind.General || !active)
            {
                throw new InvalidOperationException(
                    "La bodega general de la sede no cambia de tipo ni se desactiva: es la que recibe por defecto.");
            }
        }
        else if (kind == WarehouseKind.General)
        {
            throw new ArgumentException("Solo hay una bodega general por sede.", nameof(kind));
        }
        Rename(name, now);
        Kind = kind;
        Active = active;
    }

    private void SetCode(string code)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (c.Length is 0 or > 40)
        {
            throw new ArgumentException("El código de la bodega es obligatorio (hasta 40 caracteres).", nameof(code));
        }
        Code = c;
    }

    private void Rename(string name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre de la bodega es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        UpdatedAt = now;
    }
}

/// <summary>
/// Proveedor de mercadería (D7). Sin órdenes de compra ni cuentas por pagar: existe para saber a
/// quién se le compró cada entrada y con qué factura.
/// </summary>
public sealed class Supplier
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>RTN del proveedor: es el que aparece en su factura.</summary>
    public string? TaxId { get; private set; }

    public string? ContactName { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Supplier() { }

    public static Supplier Create(
        Guid organizationId, string name, string? taxId, string? contactName, string? phone, string? email,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var s = new Supplier { Id = Guid.CreateVersion7(now), OrganizationId = organizationId, CreatedAt = now };
        s.Update(name, taxId, contactName, phone, email, true, now);
        return s;
    }

    public void Update(
        string name, string? taxId, string? contactName, string? phone, string? email, bool active,
        TimeProvider? timeProvider = null) =>
        Update(name, taxId, contactName, phone, email, active, (timeProvider ?? TimeProvider.System).GetUtcNow());

    private void Update(
        string name, string? taxId, string? contactName, string? phone, string? email, bool active, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del proveedor es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        TaxId = Clean(taxId);
        ContactName = Clean(contactName);
        Phone = Clean(phone);
        Email = Clean(email);
        Active = active;
        UpdatedAt = now;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>
/// Configuración del inventario de una organización (P8, P9). Una fila por organización; si no
/// existe, valen los valores por defecto.
/// </summary>
public sealed class InventorySettings
{
    /// <summary>P8: un ajuste por encima de este monto (en lempiras, a costo) espera al gerente.</summary>
    public const decimal DefaultAdjustmentApprovalThreshold = 1000m;

    /// <summary>P9: un lote que vence dentro de estos días aparece en las alertas.</summary>
    public const int DefaultExpiryAlertDays = 90;

    public Guid OrganizationId { get; private set; }
    public decimal AdjustmentApprovalThreshold { get; private set; } = DefaultAdjustmentApprovalThreshold;
    public int ExpiryAlertDays { get; private set; } = DefaultExpiryAlertDays;

    /// <summary>
    /// Parte "Consumidor final" con la que se abren las cuentas de venta libre sin paciente. Se crea
    /// una vez: una parte por cada venta llenaría el padrón de clientes de filas sin nombre.
    /// </summary>
    public Guid? WalkInPartyId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private InventorySettings() { }

    public static InventorySettings Default(Guid organizationId, TimeProvider? timeProvider = null) => new()
    {
        OrganizationId = organizationId,
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
    };

    public void Update(decimal adjustmentApprovalThreshold, int expiryAlertDays, TimeProvider? timeProvider = null)
    {
        if (adjustmentApprovalThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(adjustmentApprovalThreshold), "El monto no puede ser negativo.");
        }
        if (expiryAlertDays is < 1 or > 730)
        {
            throw new ArgumentOutOfRangeException(nameof(expiryAlertDays), "Los días de alerta van de 1 a 730.");
        }
        AdjustmentApprovalThreshold = adjustmentApprovalThreshold;
        ExpiryAlertDays = expiryAlertDays;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetWalkInParty(Guid partyId, TimeProvider? timeProvider = null)
    {
        WalkInPartyId = partyId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
