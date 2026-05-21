namespace IMed.Api.Domain.Forms;

/// <summary>
/// Definición de un formulario clínico — estructura del form (schema-craft JSON)
/// + mapeo a recursos FHIR para proyección al cerrar un Encounter.
///
/// <para>Spec compartido del contrato JSON:
/// <c>imed-infra/docs/api/form-definitions-schema.md</c></para>
///
/// <para>Reglas de ciclo de vida:</para>
/// <list type="bullet">
///   <item>Se crea en estado <see cref="FormStatus.Draft"/>.</item>
///   <item>El super-usuario puede editarlo libremente mientras esté Draft.</item>
///   <item>Al publicar, transita a <see cref="FormStatus.Published"/> y queda
///         <b>inmutable</b>. Editar implica una nueva versión (mismo Code, version SemVer mayor).</item>
///   <item>Al jubilar, transita a <see cref="FormStatus.Retired"/>. Los QuestionnaireResponse
///         que referencian esa versión siguen siendo válidos pero no se renderizan
///         para nuevos encuentros.</item>
/// </list>
/// </summary>
public sealed class FormDefinition
{
    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string? SpecialtyCode { get; private set; }
    public string Version { get; private set; } = "1.0.0";
    public FormStatus Status { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Schema-craft JSON. Estructura del formulario que el frontend renderiza.</summary>
    public string SchemaJson { get; private set; } = "{}";

    /// <summary>JSON con metadata de proyección a recursos FHIR. Items, extractors, codingSystems, etc.</summary>
    public string FhirMappingJson { get; private set; } = "{}";

    /// <summary>Org dueña. Null = preset global visible para todas las orgs.</summary>
    public Guid? OrganizationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public Guid? PublishedBy { get; private set; }

    private FormDefinition() { }

    public static FormDefinition CreateDraft(
        string code,
        string title,
        string schemaJson,
        string fhirMappingJson,
        Guid createdBy,
        string? specialtyCode = null,
        string? description = null,
        Guid? organizationId = null,
        string version = "1.0.0",
        TimeProvider? timeProvider = null)
    {
        Validate(code, title, schemaJson, fhirMappingJson, version);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FormDefinition
        {
            Id = Guid.CreateVersion7(now),
            Code = code,
            SpecialtyCode = specialtyCode,
            Version = version,
            Status = FormStatus.Draft,
            Title = title,
            Description = description,
            SchemaJson = schemaJson,
            FhirMappingJson = fhirMappingJson,
            OrganizationId = organizationId,
            CreatedAt = now,
            CreatedBy = createdBy
        };
    }

    /// <summary>
    /// Edita una definición Draft. Lanza si está Published o Retired (inmutable).
    /// </summary>
    public void EditDraft(
        string title,
        string schemaJson,
        string fhirMappingJson,
        string? description,
        string? specialtyCode)
    {
        if (Status != FormStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Solo se pueden editar definiciones en Draft. Estado actual: {Status}.");
        }
        Validate(Code, title, schemaJson, fhirMappingJson, Version);
        Title = title;
        SchemaJson = schemaJson;
        FhirMappingJson = fhirMappingJson;
        Description = description;
        SpecialtyCode = specialtyCode;
    }

    /// <summary>Publica la definición. Inmutable a partir de acá.</summary>
    public void Publish(Guid publishedBy, TimeProvider? timeProvider = null)
    {
        if (Status != FormStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Solo se publican definiciones Draft. Estado actual: {Status}.");
        }
        Status = FormStatus.Published;
        PublishedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        PublishedBy = publishedBy;
    }

    /// <summary>Retira una definición publicada. Encounter previos siguen referenciándola para auditoría.</summary>
    public void Retire()
    {
        if (Status != FormStatus.Published)
        {
            throw new InvalidOperationException(
                $"Solo se retiran definiciones Published. Estado actual: {Status}.");
        }
        Status = FormStatus.Retired;
    }

    private static void Validate(string code, string title, string schemaJson, string fhirMappingJson, string version)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code es obligatorio.", nameof(code));
        }
        if (code.Length > 60)
        {
            throw new ArgumentException("Code máximo 60 caracteres.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title es obligatorio.", nameof(title));
        }
        if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson.Trim() == "{}")
        {
            throw new ArgumentException("SchemaJson no puede estar vacío.", nameof(schemaJson));
        }
        if (string.IsNullOrWhiteSpace(fhirMappingJson))
        {
            throw new ArgumentException("FhirMappingJson es obligatorio (puede ser '{\"items\":[]}' si no hay mapeos).", nameof(fhirMappingJson));
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Version es obligatoria (usar SemVer ej. '1.0.0').", nameof(version));
        }
    }
}

public enum FormStatus
{
    Draft = 0,
    Published = 1,
    Retired = 2,
}
