namespace IMed.Api.Domain.Billing.Catalog;

/// <summary>Qué clase de cosa se cobra.</summary>
public enum BillableItemKind
{
    /// <summary>Consulta, procedimiento, examen. No descuenta inventario.</summary>
    Service = 0,

    /// <summary>Medicamento o insumo. Al cobrarlo hay que mover stock (U2/U3).</summary>
    Product = 1,

    /// <summary>Paquete cerrado: un precio fijo que consume N componentes.</summary>
    Package = 2,

    /// <summary>Honorario profesional, separable del componente técnico.</summary>
    ProfessionalFee = 3,

    /// <summary>Estancia hospitalaria por día.</summary>
    RoomAndBoard = 4,

    /// <summary>Cuota o cargo administrativo.</summary>
    Fee = 5,
}

/// <summary>
/// Cómo se parte un estudio entre quien pone el equipo y quien pone la firma.
///
/// <para>Existe para no duplicar el catálogo: una radiografía puede cobrarse entera
/// (<see cref="Global"/>) o partida en lectura y toma, y son el mismo estudio.</para>
/// </summary>
public enum BillableComponent
{
    Global = 0,
    Technical = 1,
    Professional = 2,
}

/// <summary>
/// Cuándo se cobra (hueco G2). Es lo que materializa la puerta de prepago de FAC-7.
/// </summary>
public enum PaymentTiming
{
    /// <summary>Se cobra antes de ejecutarlo. El laboratorio ambulatorio típico.</summary>
    Prepaid = 0,

    /// <summary>Se ejecuta y se cobra después. Lo hospitalario y lo de urgencia.</summary>
    Postpaid = 1,

    /// <summary>Da igual: no bloquea nunca.</summary>
    Either = 2,
}

/// <summary>Para qué sirve un tarifario. Decide su lugar en la resolución de precio.</summary>
public enum PriceListKind
{
    /// <summary>El precio de mostrador. Es el último recurso antes del precio base del ítem.</summary>
    Public = 0,

    /// <summary>Tarifario de un convenio con aseguradora o empresa. Gana sobre todos.</summary>
    Agreement = 1,

    /// <summary>Promoción por temporada. Gana sobre el público.</summary>
    Promotional = 2,

    /// <summary>Precio para el personal de la clínica.</summary>
    Employee = 3,
}

/// <summary>
/// De dónde salió el precio que se cobró.
///
/// <para><b>Se guarda en el cargo, no se recalcula.</b> "¿Por qué a este paciente se le cobró
/// esto?" es la pregunta más frecuente en una auditoría de facturación, y responderla volviendo
/// a correr el algoritmo con el catálogo de hoy contesta otra pregunta distinta.</para>
/// </summary>
public enum PriceSource
{
    /// <summary>Tarifario de convenio del pagador.</summary>
    AgreementList = 0,

    /// <summary>Lista promocional vigente.</summary>
    PromotionalList = 1,

    /// <summary>Precio de personal.</summary>
    EmployeeList = 2,

    /// <summary>Lista pública.</summary>
    PublicList = 3,

    /// <summary>Precio base del ítem: no había tarifario que aplicara.</summary>
    ItemDefault = 4,
}

/// <summary>
/// Tipo de consulta (plan de salida a producción, D4). El precio de una consulta depende de la
/// especialidad <b>y</b> del tipo: cada combinación es un cobrable propio del catálogo, así los
/// tarifarios la tratan como a cualquier otro servicio.
/// </summary>
public enum ConsultationType
{
    /// <summary>Primera vez con esa especialidad (o fuera de la ventana de control).</summary>
    FirstVisit = 0,

    /// <summary>Control o reconsulta dentro de la ventana configurada.</summary>
    FollowUp = 1,

    /// <summary>Emergencia. Solo se elige a mano.</summary>
    Emergency = 2,
}

/// <summary>
/// Cómo viaja el tipo de consulta en FHIR: <c>Encounter.type</c> y <c>Appointment.appointmentType</c>
/// con este sistema y estos códigos (A3).
/// </summary>
public static class ConsultationTypeCodes
{
    public const string System = "https://imed.app/fhir/CodeSystem/consultation-type";

    /// <summary>Medicina general y de familia: la consulta de quien no declaró especialidad (P2).</summary>
    public const string GeneralPracticeSpecialty = "394814009";

    public static string ToCode(ConsultationType type) => type switch
    {
        ConsultationType.FirstVisit => "first-visit",
        ConsultationType.FollowUp => "follow-up",
        ConsultationType.Emergency => "emergency",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ConsultationType? FromCode(string? code) => code switch
    {
        "first-visit" => ConsultationType.FirstVisit,
        "follow-up" => ConsultationType.FollowUp,
        "emergency" => ConsultationType.Emergency,
        _ => null,
    };

    public static string Display(ConsultationType type) => type switch
    {
        ConsultationType.FirstVisit => "Primera vez",
        ConsultationType.FollowUp => "Control",
        ConsultationType.Emergency => "Emergencia",
        _ => type.ToString(),
    };
}
