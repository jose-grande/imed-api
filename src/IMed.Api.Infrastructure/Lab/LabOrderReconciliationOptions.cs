namespace IMed.Api.Infrastructure.Lab;

/// <summary>Configuración del reconciliador de órdenes de laboratorio. Sección <c>LabOrderReconciliation</c>.</summary>
public sealed class LabOrderReconciliationOptions
{
    public const string SectionName = "LabOrderReconciliation";

    /// <summary>
    /// Encendido por defecto. Es una red de seguridad, y una red que hay que acordarse de
    /// desplegar activada no es una red.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cada cuánto se revisa. No hace falta que sea frecuente: repara, no atiende.</summary>
    public int PollSeconds { get; set; } = 300;

    /// <summary>
    /// Cuánto se espera antes de considerar una orden abandonada.
    ///
    /// <para>La promoción normal la hace el cliente en milisegundos al cerrar la consulta. Este
    /// margen existe para no competir con ella: sin él, el reconciliador podría escribir la misma
    /// orden al mismo tiempo que el navegador y provocar un conflicto de versión donde no había
    /// ningún problema.</para>
    /// </summary>
    public int GraceMinutes { get; set; } = 10;

    /// <summary>Tope de órdenes reparadas por vuelta, para que un backlog no monopolice el proceso.</summary>
    public int MaxPerRun { get; set; } = 100;
}
