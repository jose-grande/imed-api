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

    // --- Facturación (U1 · FAC-1) -------------------------------------------
    // Se declaran los cuatro de una vez aunque FAC-1 no tenga todavía una pantalla que cobre:
    // son los que la consola del módulo y el guard consultan para decidir qué estación mostrar,
    // y agregarlos de a uno obligaría a re-sembrar la matriz en cada épica.
    public const string BillingRead = "billing.read";
    public const string BillingCharge = "billing.charge";
    public const string BillingCollect = "billing.collect";
    public const string BillingSupervise = "billing.supervise";

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

        (BillingRead, "billing", "Ver el módulo de facturación: cuentas, cargos, documentos"),
        (BillingCharge, "billing", "Abrir cuentas y capturar cargos"),
        (BillingCollect, "billing", "Cobrar, emitir documentos y arquear el propio turno"),
        (BillingSupervise, "billing", "Autorizar descuentos y anulaciones, gestionar CAI y arqueos ajenos"),

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
        ("org_admin", RecordWrite),
        ("org_admin", BillingRead), ("org_admin", BillingCharge), ("org_admin", BillingCollect), ("org_admin", BillingSupervise),
        ("org_admin", OrgManage), ("org_admin", OrgTeamManage), ("org_admin", OrgLocationsManage), ("org_admin", OrgPolicyManage),

        // El médico ve el laboratorio y el expediente. En una clínica chica el de turno cubre a
        // todos los pacientes; la contención real es el registro de accesos, no la restricción.
        ("practitioner", LabRead), ("practitioner", RecordSearchDemographics),
        ("practitioner", RecordReadAny), ("practitioner", LabResultReadOrdered),
        ("practitioner", RecordWrite),

        // Recepción necesita encontrar al paciente para agendar, no leer su historia.
        ("auxiliar", RecordSearchDemographics), ("auxiliar", RecordWrite),

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

        // `pharmacist`, `pharmacy_tech`, `warehouse_keeper` e `inventory_manager` se declaran en
        // `Roles` sin permisos base: sus módulos son U2 y U3. Existir sin conceder nada los deja
        // configurables desde la pantalla de la matriz —que es el punto de tenerlos listados— sin
        // abrir acceso a nada mientras tanto.

        // Soporte interno de IMed: acceso elevado, auditado. Sin permisos de expediente por
        // defecto — que soporte pueda leer historias clínicas debe ser una decisión explícita.
        ("imed_superadmin", OrgManage), ("imed_superadmin", OrgTeamManage),
        ("imed_superadmin", OrgLocationsManage), ("imed_superadmin", OrgPolicyManage),
        ("imed_superadmin", LabManage),
    ];
}
