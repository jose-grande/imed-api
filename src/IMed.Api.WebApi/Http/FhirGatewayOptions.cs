namespace IMed.Api.WebApi.Http;

/// <summary>Cómo se comporta la política del proxy FHIR (ADR-007, SEC-3).</summary>
public enum FhirGatewayMode
{
    /// <summary>
    /// Evalúa y registra qué habría bloqueado, pero deja pasar todo.
    ///
    /// <para>Es el arranque obligatorio del plan. Un olvido en la lista blanca tiene que aparecer
    /// en un log y no en la cara de un técnico: se corre así con uso real, se revisa lo que se
    /// habría negado, y recién entonces se pasa a <see cref="Enforce"/>.</para>
    /// </summary>
    Shadow = 0,

    /// <summary>Bloquea lo que la política no permite.</summary>
    Enforce = 1,
}

public sealed class FhirGatewayOptions
{
    public const string SectionName = "FhirGateway";

    /// <summary>
    /// Arranca en sombra por defecto. Que el modo seguro sea el default importa: una instalación
    /// que olvide configurar esto no deja a nadie afuera, solo deja de proteger — y eso se ve en
    /// el log, mientras que lo contrario se ve en soporte.
    /// </summary>
    public FhirGatewayMode Mode { get; set; } = FhirGatewayMode.Shadow;

    /// <summary>
    /// Modo de la clasificación por forma de consulta (SEC-4), independiente del anterior.
    ///
    /// <para><b>Por qué es un modo aparte.</b> El proxy ya está en <c>Enforce</c> y verificado con
    /// tres pasadas de uso real. La clasificación por forma es nueva y toca <b>todas</b> las
    /// lecturas, no un recurso: cualquier forma de consulta que el catálogo no haya previsto se
    /// convertiría en un 403 en pantalla. Con un solo modo, ponerla en sombra habría obligado a
    /// bajar también lo ya ganado.</para>
    ///
    /// <para>En sombra tampoco se aplica la proyección de identificación: sombra tiene que
    /// significar "no cambia el comportamiento", y recortar campos lo cambia.</para>
    /// </summary>
    public FhirGatewayMode IntentMode { get; set; } = FhirGatewayMode.Shadow;

    /// <summary>Habilita el proxy. Apagado, `/fhir/**` no se sirve desde el gateway.</summary>
    public bool Enabled { get; set; } = true;
}
