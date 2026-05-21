namespace IMed.Api.Infrastructure.Terminology.Importers;

/// <summary>
/// Importer interno del adapter Postgres que carga un catálogo de terminología
/// desde una fuente externa (CSV, TSV, archivo XML, dump SQL, etc.) hacia las
/// tablas locales del adapter.
///
/// <para>NO es port en Application — es detalle interno del adapter Postgres.
/// El día que migremos a HAPI Terminology Server o Snowstorm, los importers
/// dejan de ser relevantes (la fuente de verdad sale de imed-api).</para>
///
/// <para>Múltiples implementaciones pueden coexistir para el mismo CodeSystem
/// si el catálogo viene en distintos formatos (ej: WHO Simple Tabulation TSV
/// vs WHO Foundation API JSON para ICD-11). El seeder elige cuál usar según
/// la config de origen del archivo.</para>
/// </summary>
public interface ITerminologyCatalogImporter
{
    /// <summary>URI canónico del CodeSystem que importa este implementador.</summary>
    string CodeSystem { get; }

    /// <summary>
    /// Identificador del formato de origen — "who-simple-tabulation",
    /// "loinc-standard", etc. Permite que un mismo seeder elija entre múltiples
    /// importers del mismo system mirando la config.
    /// </summary>
    string SourceFormat { get; }

    /// <summary>
    /// Importa el catálogo desde el path indicado.
    ///
    /// <para>El significado del path depende del implementador:</para>
    /// <list type="bullet">
    ///   <item>Importers de archivo único (ej: ICD-11 WHO Simple Tabulation): path al archivo.</item>
    ///   <item>Importers de release multi-archivo (ej: LOINC): path al directorio raíz del release; el importer resuelve los archivos hijos.</item>
    /// </list>
    /// </summary>
    Task<ImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken);
}

public sealed record ImportResult(int Inserted, int Skipped, int Errors)
{
    public static ImportResult Empty { get; } = new(0, 0, 0);
}
