namespace IMed.Api.Domain.Authorization;

/// <summary>
/// Una forma de consulta declarada: qué recurso, qué verbo, qué parámetros y con qué método se
/// habilita (ADR-007 §4, SEC-4).
///
/// <para><b>Por qué la forma y no solo el recurso.</b> SEC-3 dejó la puerta en "¿está habilitado
/// <c>Search</c> sobre <c>Observation</c>?". Eso no distingue entre pedir los signos vitales de un
/// paciente y pedir cualquier observación con cualquier filtro. El intent describe la consulta
/// entera, así que un parámetro que nadie declaró no viaja — que es por donde se filtra la
/// información en sistemas de salud.</para>
///
/// <para><b>No hay cajón de sastre.</b> Lo que no encaja con ningún intent se niega. Es la
/// diferencia entre una lista blanca y una lista de casos conocidos: sin esta propiedad, una forma
/// que nadie previó entra igual, solo que exigiendo el permiso más amplio — y los roles clínicos lo
/// tienen.</para>
/// </summary>
/// <param name="Name">
/// Nombre con el que se audita. Se elige desde el lado del usuario (<c>agenda.mine</c>,
/// <c>patient.find</c>) y no desde el técnico (<c>Search:Appointment</c>), porque la pregunta que
/// esto tiene que contestar es "¿quién abrió la agenda de otro?" y no "¿hubo búsquedas de citas?".
/// </param>
/// <param name="Method">Permiso que la habilita. Ver <see cref="FhirAccessMethods"/>.</param>
/// <param name="RequiredAnyOf">
/// Al menos uno de estos parámetros tiene que venir. Vacío = la consulta no necesita filtro, lo que
/// la vuelve una enumeración y por eso suele exigir un método amplio.
/// </param>
/// <param name="Allowed">Parámetros adicionales aceptados. Todo lo demás rechaza el encaje.</param>
/// <param name="RequiredValues">
/// Parámetros cuyo <b>valor</b> está acotado. Sin esto, exigir que venga <c>category</c> sin mirar
/// qué dice dejaría que <c>category=laboratory</c> entrara por el intent de signos vitales.
/// </param>
/// <param name="SelfPractitionerParam">
/// Parámetro que tiene que referenciar al <c>Practitioner</c> del propio llamante. Es lo único que
/// se puede afirmar sobre pertenencia sin salir del proceso: quién sos se sabe por el token, de
/// quién es un expediente habría que preguntárselo a HAPI en cada lectura.
/// </param>
/// <param name="Projection">
/// Valor de <c>_elements</c> a forzar en la consulta saliente, o null para no recortar.
/// </param>
public sealed record FhirIntent(
    string Name,
    string ResourceType,
    FhirVerb Verb,
    string Method,
    IReadOnlySet<string>? RequiredAnyOf = null,
    IReadOnlySet<string>? Allowed = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? RequiredValues = null,
    string? SelfPractitionerParam = null,
    string? Projection = null)
{
    /// <summary>
    /// Parámetros que ningún intent necesita declarar. Son de dos familias distintas:
    ///
    /// <list type="bullet">
    ///   <item><b>Paginado y orden</b> — dicen cuántos y en qué orden, no qué. El tope de
    ///   <c>_count</c> lo sigue validando el acotado de SEC-3, que corre antes.</item>
    ///   <item><b><c>_include</c> y <c>_revinclude</c></b> — los gobierna
    ///   <see cref="FhirAccessCatalog.AllowedIncludes"/> en la puerta 5, que ya los compara contra
    ///   un conjunto cerrado por recurso. Declararlos también acá sería tener dos listas blancas
    ///   para lo mismo, y dos listas que hay que mantener sincronizadas es una que se va a
    ///   desincronizar.</item>
    /// </list>
    /// </summary>
    public static IReadOnlySet<string> UndeclaredButAllowed { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_count", "_offset", "_sort", "_summary", "_total",
            "_include", "_revinclude",
        };

    /// <summary>
    /// Nombre de parámetro sin su modificador: <c>name:contains</c> es <c>name</c>.
    ///
    /// <para>FHIR pega el modificador a la clave. Comparar la clave entera dejaría afuera a
    /// <c>name:contains</c> —que el buscador de profesionales usa— y, peor, dejaría entrar
    /// cualquier modificador sobre un parámetro declarado sin que nadie lo mirara.</para>
    /// </summary>
    public static string Root(string parameter) => parameter.Split(':', 2)[0];
}

/// <summary>
/// Los métodos de consulta y qué cubre cada uno (ADR-007 §4).
///
/// <para>El orden importa y es la pieza que evita romper la aplicación: la agenda manda
/// <c>Appointment?actor=Practitioner/…</c>, que es el intent <c>agenda.mine</c> y exige
/// <c>record.read.own</c>. Nadie tiene ese permiso en la matriz base — <c>practitioner</c> tiene
/// <c>record.read.any</c>. Sin la relación de cobertura, el primer médico que abriera su agenda
/// recibiría un 403.</para>
/// </summary>
public static class FhirAccessMethods
{
    /// <summary>
    /// Familia de lectura de expediente, de más amplia a más acotada. Tener una cubre las de abajo.
    /// </summary>
    private static readonly string[] ReadLadder =
    [
        PermissionCatalog.RecordReadAny,
        PermissionCatalog.RecordReadOwn,
        PermissionCatalog.RecordSearchDemographics,
    ];

    /// <summary>
    /// Si los permisos del usuario alcanzan para el método exigido.
    ///
    /// <para>Fuera de la familia de lectura no hay jerarquía: <c>record.write</c> u
    /// <c>org.team.manage</c> se tienen o no se tienen.</para>
    /// </summary>
    public static bool Satisfies(Func<string, bool> holds, string requiredMethod)
    {
        var rung = Array.IndexOf(ReadLadder, requiredMethod);
        if (rung < 0)
        {
            return holds(requiredMethod);
        }

        for (var i = 0; i <= rung; i++)
        {
            if (holds(ReadLadder[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Si el usuario tiene lectura amplia de expediente. Decide si se aplica la proyección: quien
    /// puede ver cualquier expediente no gana nada recibiendo el paciente recortado.
    /// </summary>
    public static bool HasBroadRecordRead(Func<string, bool> holds) =>
        holds(PermissionCatalog.RecordReadAny);
}
