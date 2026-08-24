namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Aprovisiona el catálogo de laboratorio de una organización recién creada
/// clonando las plantillas globales (org_id NULL) a filas propias de la org
/// (pruebas, rangos, paneles+versiones+items, reglas+versión, fórmulas, TAT,
/// motivos de rechazo y formatos de etiqueta). Los tipos de muestra y tubos NO se
/// clonan: son datos de referencia estándar compartidos.
///
/// <para>Modelo <b>copy-on-create</b> (reemplaza el "global visible a todos" de
/// LIS-1): tras clonar, cada org solo ve/edita lo suyo. Idempotente — si la org ya
/// tiene su catálogo, es no-op — para poder correr en el bootstrap y en un backfill
/// de arranque sin duplicar.</para>
/// </summary>
public interface ILabCatalogProvisioning
{
    /// <summary>
    /// Clona las plantillas globales al catálogo propio de <paramref name="organizationId"/>
    /// si aún no lo tiene. Devuelve <c>true</c> si aprovisionó, <c>false</c> si ya estaba.
    /// </summary>
    Task<bool> ProvisionOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
