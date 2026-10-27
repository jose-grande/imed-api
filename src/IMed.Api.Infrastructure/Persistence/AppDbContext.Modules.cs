namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Punto de extensión del <see cref="AppDbContext"/> para los módulos que mapean fuera del archivo
/// principal (plan de salida a producción, F0.6).
///
/// <para><b>Cómo se agrega un módulo:</b> sus <c>DbSet</c> van en un
/// <c>AppDbContext.&lt;Módulo&gt;.cs</c> (otra parte de esta clase parcial) y su mapeo en clases
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> bajo el namespace
/// <c>IMed.Api.Infrastructure.Persistence.Configurations.&lt;Módulo&gt;</c>, que
/// <c>OnModelCreating</c> aplica solo. La convención snake_case se aplica después, igual que al
/// resto.</para>
/// </summary>
public sealed partial class AppDbContext
{
    /// <summary>Namespace raíz de las configuraciones que se aplican por ensamblado.</summary>
    internal const string ConfigurationsNamespace = "IMed.Api.Infrastructure.Persistence.Configurations";
}
