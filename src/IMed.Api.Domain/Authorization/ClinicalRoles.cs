namespace IMed.Api.Domain.Authorization;

/// <summary>
/// Roles que hacen de un miembro un <b>profesional</b> en sentido FHIR (Practitioner): quien
/// atiende pacientes o ejecuta y firma estudios de laboratorio. Solo ellos reciben un
/// Practitioner al registrarse; el resto del personal (administración, caja, recepción, farmacia,
/// inventario) es miembro de la organización sin perfil clínico.
///
/// <para>Decisión del dueño (2026-11-02): roles clínicos y de laboratorio, nadie más.</para>
/// </summary>
public static class ClinicalRoles
{
    public static IReadOnlyList<string> All { get; } =
    [
        // Clínica
        "practitioner",
        "auxiliar",
        // Laboratorio: ejecutan, validan y firman resultados.
        "lab_supervisor",
        "bioanalyst",
        "pathologist",
        "lab_tech",
    ];

    /// <summary>¿Alguno de estos roles requiere un Practitioner?</summary>
    public static bool RequirePractitioner(IEnumerable<string> roles) =>
        roles.Any(r => All.Contains(r, StringComparer.Ordinal));
}
