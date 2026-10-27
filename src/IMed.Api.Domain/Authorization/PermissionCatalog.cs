namespace IMed.Api.Domain.Authorization;

/// <summary>
/// Catálogo de permisos y matriz base del producto (ADR-007).
///
/// <para><b>Por qué vive en código.</b> Es la política del producto, no dato del tenant: tiene que
/// tener historial en git para poder contestar quién cambió qué regla y cuándo. Se siembra en la
/// base para que sea consultable y sobreescribible por organización, pero el origen es este archivo.</para>
///
/// <para><b>Paridad al migrar.</b> Los cuatro permisos de laboratorio reproducen exactamente los
/// roles que tenían las políticas quemadas en <c>Program.cs</c> antes de SEC-2. La migración cambia
/// dónde vive la política, no quién puede qué — eso se decide después y a la vista.</para>
/// </summary>
public static class PermissionCatalog
{
    // --- Laboratorio: equivalen 1:1 a las políticas previas -----------------
    public const string LabRead = "lab.read";
    public const string LabOperate = "lab.operate";
    public const string LabValidate = "lab.validate";
    public const string LabManage = "lab.manage";

    // --- Métodos de consulta de expediente (ADR-007 §4) ---------------------
    // Se siembran ahora y los consume SEC-4: el alcance de un acceso se declara
    // en la puerta de entrada, en vez de evaluarse como predicado en cada lectura.
    public const string RecordSearchDemographics = "record.search.demographics";
    public const string RecordReadOwn = "record.read.own";
    public const string RecordReadAssigned = "record.read.assigned";
    public const string RecordReadLocation = "record.read.location";
    public const string RecordReadAny = "record.read.any";
    public const string RecordReadEmergency = "record.read.emergency";
    public const string LabResultReadOrdered = "lab.result.read.ordered";
    public const string LabResultReadAny = "lab.result.read.any";

    /// <summary>Crear y modificar datos del expediente: paciente, encuentro, alergias, recetas.</summary>
    public const string RecordWrite = "record.write";

    /// <summary>
    /// Capturar preclínica (Épica P-1): signos vitales y alergias de un paciente <b>con cita en la
    /// fila</b>. Existe aparte de <see cref="RecordReadAny"/> porque la auxiliar necesita ver las
    /// alergias ya registradas para no duplicarlas, y eso no justifica abrirle el expediente entero:
    /// el endpoint que lo consume solo lee al paciente de una cita en curso.
    /// </summary>
    public const string RecordPreclinic = "record.preclinic";

    // --- Facturación (U1 · FAC-1) -------------------------------------------
    // Se declaran los cuatro de una vez aunque FAC-1 no tenga todavía una pantalla que cobre:
    // son los que la consola del módulo y el guard consultan para decidir qué estación mostrar,
    // y agregarlos de a uno obligaría a re-sembrar la matriz en cada épica.
    public const string BillingRead = "billing.read";
    public const string BillingCharge = "billing.charge";
    public const string BillingCollect = "billing.collect";
    public const string BillingSupervise = "billing.supervise";

    // --- Inventario y farmacia (plan de salida a producción, F0.7) -----------
    /// <summary>Ver existencias, lotes, kardex, artículos, bodegas y proveedores.</summary>
    public const string InventoryRead = "inventory.read";

    /// <summary>Mover inventario: entradas de compra, traslados, consumo interno y conteos.</summary>
    public const string InventoryOperate = "inventory.operate";

    /// <summary>Configurar el inventario (artículos, bodegas, proveedores) y aprobar ajustes.</summary>
    public const string InventoryManage = "inventory.manage";

    /// <summary>Despachar las recetas de las consultas.</summary>
    public const string PharmacyDispense = "pharmacy.dispense";

    /// <summary>Vender en mostrador artículos de venta libre.</summary>
    public const string PharmacySell = "pharmacy.sell";

    // --- Administración -----------------------------------------------------
    public const string OrgManage = "org.manage";
    public const string OrgTeamManage = "org.team.manage";
    public const string OrgLocationsManage = "org.locations.manage";
    public const string OrgPolicyManage = "org.policy.manage";

    /// <summary>
    /// Roles del realm, en el orden en que conviene mostrarlos: de más amplio a más acotado.
    ///
    /// <para>Vive acá y no se descubre de la base porque los roles los define Keycloak, y una
    /// lista derivada de las filas existentes ocultaría un rol al que nadie le concedió nada
    /// todavía — justo el que hay que poder configurar.</para>
    /// </summary>
    public static IReadOnlyList<string> Roles =>
    [
        "org_admin",
        "practitioner",
        "lab_supervisor",
        "bioanalyst",
        "pathologist",
        "lab_tech",
        "auxiliar",
        "billing_supervisor",
        "billing_clerk",
        "cashier",
        "pharmacist",
        "pharmacy_tech",
        "inventory_manager",
        "warehouse_keeper",
        "imed_superadmin",
    ];

    public static IReadOnlyList<(string Code, string Module, string Description)> All =>
    [
        (LabRead, "lab", "Ver el laboratorio: worklists, órdenes, resultados"),
        (LabOperate, "lab", "Operar el laboratorio: recibir muestras, capturar resultados"),
        (LabValidate, "lab", "Validar y liberar informes"),
        (LabManage, "lab", "Configurar el laboratorio: catálogo, reglas, formatos"),

        (RecordSearchDemographics, "record", "Buscar pacientes y ver solo su identificación"),
        (RecordReadOwn, "record", "Ver expedientes donde el profesional firmó algo"),
        (RecordReadAssigned, "record", "Ver expedientes de pacientes asignados"),
        (RecordReadLocation, "record", "Ver expedientes atendidos en su sede"),
        (RecordReadAny, "record", "Ver cualquier expediente de la organización"),
        (RecordReadEmergency, "record", "Romper el vidrio: acceso de emergencia con justificación"),
        (LabResultReadOrdered, "lab", "Ver resultados de las órdenes que solicitó"),
        (LabResultReadAny, "lab", "Ver cualquier resultado del laboratorio"),
        (RecordWrite, "record", "Crear y modificar datos del expediente"),
        (RecordPreclinic, "record", "Capturar preclínica de pacientes con cita en curso"),

        (BillingRead, "billing", "Ver el módulo de facturación: cuentas, cargos, documentos"),
        (BillingCharge, "billing", "Abrir cuentas y capturar cargos"),
        (BillingCollect, "billing", "Cobrar, emitir documentos y arquear el propio turno"),
        (BillingSupervise, "billing", "Autorizar descuentos y anulaciones, gestionar CAI y arqueos ajenos"),

        (InventoryRead, "inventory", "Ver existencias, lotes, kardex, artículos y proveedores"),
        (InventoryOperate, "inventory", "Registrar entradas de compra, traslados, consumo interno y conteos"),
        (InventoryManage, "inventory", "Configurar artículos, bodegas y proveedores, y aprobar ajustes"),
        (PharmacyDispense, "pharmacy", "Despachar recetas de las consultas"),
        (PharmacySell, "pharmacy", "Vender en mostrador artículos de venta libre"),

        (OrgManage, "org", "Administrar la organización"),
        (OrgTeamManage, "org", "Administrar el equipo y las invitaciones"),
        (OrgLocationsManage, "org", "Administrar las sedes"),
        (OrgPolicyManage, "org", "Administrar la matriz de roles y permisos"),
    ];

    /// <summary>
    /// Matriz base rol → permisos. Los cuatro de laboratorio reproducen exactamente
    /// <c>LabRead</c>/<c>LabTechnician</c>/<c>LabValidator</c>/<c>LabSupervisor</c>.
    /// </summary>
    public static IReadOnlyList<(string Role, string Permission)> BaseMatrix =>
    [
        // org_admin administra su organización y puede hacer todo lo operativo.
        ("org_admin", LabRead), ("org_admin", LabOperate), ("org_admin", LabValidate), ("org_admin", LabManage),
        ("org_admin", RecordSearchDemographics), ("org_admin", RecordReadAny), ("org_admin", LabResultReadAny),
        ("org_admin", RecordWrite), ("org_admin", RecordPreclinic),
        ("org_admin", BillingRead), ("org_admin", BillingCharge), ("org_admin", BillingCollect), ("org_admin", BillingSupervise),
        ("org_admin", OrgManage), ("org_admin", OrgTeamManage), ("org_admin", OrgLocationsManage), ("org_admin", OrgPolicyManage),
        ("org_admin", InventoryRead), ("org_admin", InventoryOperate), ("org_admin", InventoryManage),
        ("org_admin", PharmacyDispense), ("org_admin", PharmacySell),

        // El médico ve el laboratorio y el expediente. En una clínica chica el de turno cubre a
        // todos los pacientes; la contención real es el registro de accesos, no la restricción.
        ("practitioner", LabRead), ("practitioner", RecordSearchDemographics),
        ("practitioner", RecordReadAny), ("practitioner", LabResultReadOrdered),
        ("practitioner", RecordWrite), ("practitioner", RecordPreclinic),

        // Recepción necesita encontrar al paciente para agendar, no leer su historia.
        // La preclínica sí necesita las alergias ya registradas, pero solo las del paciente que
        // tiene enfrente: por eso un permiso propio y no lectura amplia.
        ("auxiliar", RecordSearchDemographics), ("auxiliar", RecordWrite), ("auxiliar", RecordPreclinic),

        ("lab_tech", LabRead), ("lab_tech", LabOperate),
        ("lab_tech", RecordSearchDemographics), ("lab_tech", LabResultReadAny),

        ("bioanalyst", LabRead), ("bioanalyst", LabValidate),
        ("bioanalyst", RecordSearchDemographics), ("bioanalyst", LabResultReadAny),

        ("pathologist", LabRead), ("pathologist", LabValidate),
        ("pathologist", RecordSearchDemographics), ("pathologist", LabResultReadAny),

        ("lab_supervisor", LabRead), ("lab_supervisor", LabOperate), ("lab_supervisor", LabValidate),
        ("lab_supervisor", LabManage), ("lab_supervisor", RecordSearchDemographics),
        ("lab_supervisor", LabResultReadAny), ("lab_supervisor", OrgLocationsManage),

        // Facturación (U1). Los tres reciben `record.search.demographics` y nada más del
        // expediente: para cobrar una consulta hay que poder identificar al paciente, pero no hay
        // razón para que caja lea su historia. La forma `patient.find` de SEC-4 ya recorta la
        // respuesta a los campos de identificación cuando el que pregunta no puede leer todo.
        ("cashier", BillingRead), ("cashier", BillingCollect),
        ("cashier", RecordSearchDemographics),

        ("billing_clerk", BillingRead), ("billing_clerk", BillingCharge),
        ("billing_clerk", RecordSearchDemographics),

        ("billing_supervisor", BillingRead), ("billing_supervisor", BillingCharge),
        ("billing_supervisor", BillingCollect), ("billing_supervisor", BillingSupervise),
        ("billing_supervisor", RecordSearchDemographics),

        // Inventario y farmacia (F0.7). Farmacia identifica al paciente para despachar o vender,
        // pero no lee el expediente: la receta le llega por su propio endpoint.
        ("inventory_manager", InventoryRead), ("inventory_manager", InventoryOperate),
        ("inventory_manager", InventoryManage),
        ("warehouse_keeper", InventoryRead), ("warehouse_keeper", InventoryOperate),
        ("pharmacist", PharmacyDispense), ("pharmacist", PharmacySell),
        ("pharmacist", InventoryRead), ("pharmacist", InventoryOperate),
        ("pharmacist", RecordSearchDemographics),
        ("pharmacy_tech", PharmacyDispense), ("pharmacy_tech", PharmacySell),
        ("pharmacy_tech", InventoryRead),
        ("pharmacy_tech", RecordSearchDemographics),

        // Soporte interno de IMed: acceso elevado, auditado. Sin permisos de expediente por
        // defecto — que soporte pueda leer historias clínicas debe ser una decisión explícita.
        ("imed_superadmin", OrgManage), ("imed_superadmin", OrgTeamManage),
        ("imed_superadmin", OrgLocationsManage), ("imed_superadmin", OrgPolicyManage),
        ("imed_superadmin", LabManage),
    ];
}
