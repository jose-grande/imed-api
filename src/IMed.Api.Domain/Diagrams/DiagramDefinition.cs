namespace IMed.Api.Domain.Diagrams;

/// <summary>
/// Definición de un diagrama interactivo (odontograma, esqueleto, etc.) — un SVG
/// con zonas clickeables (<c>zones_json</c>, formato <c>DiagramZone[]</c> de
/// <c>diagram-engine</c>) más las referencias a los formularios que se abren al
/// clickear una zona (<c>linked_form_codes_json</c>, codes de <see cref="Forms.FormDefinition"/>
/// — no se embeben, se referencian) y el mapeo zona→sitio anatómico
/// (<c>zone_body_site_map_json</c>, ej. <c>{"tooth-16": {"system":"FDI","code":"16"}}</c>)
/// que usa <c>EncounterCloseFlow</c> para poblar <c>Procedure.bodySite</c> cuando
/// el formulario vinculado a esa zona no trae su propio campo de sitio anatómico.
///
/// <para>Mismo ciclo de vida que <see cref="Forms.FormDefinition"/>
/// (Draft → Published → Retired) y mismo patrón de resolución por especialidad —
/// pero a diferencia de un form clínico (uno solo por encuentro), una especialidad
/// puede tener <b>varios</b> diagramas activos a la vez (por eso la resolución por
/// especialidad devuelve una lista, no un único resultado).</para>
/// </summary>
public sealed class DiagramDefinition
{
    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string? SpecialtyCode { get; private set; }
    public string Version { get; private set; } = "1.0.0";
    public DiagramStatus Status { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Contenido SVG completo — elementos zona deben tener <c>id</c> único.</summary>
    public string SvgContent { get; private set; } = string.Empty;

    /// <summary>JSON <c>DiagramZone[]</c> (svgId, label, states, defaultState, styles).</summary>
    public string ZonesJson { get; private set; } = "[]";

    /// <summary>JSON <c>AnnotationStyle</c> por defecto para las anotaciones libres. Opcional.</summary>
    public string AnnotationStyleJson { get; private set; } = "{}";

    /// <summary>Array JSON de codes de <see cref="Forms.FormDefinition"/> vinculados (ej. <c>["odonto-caries","odonto-endodoncia"]</c>).</summary>
    public string LinkedFormCodesJson { get; private set; } = "[]";

    /// <summary>Mapa JSON <c>{ svgId: {system, code, display?} }</c> — sitio anatómico por zona.</summary>
    public string ZoneBodySiteMapJson { get; private set; } = "{}";

    /// <summary>Org dueña. Null = preset global visible para todas las orgs.</summary>
    public Guid? OrganizationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public Guid? PublishedBy { get; private set; }

    private DiagramDefinition() { }

    public static DiagramDefinition CreateDraft(
        string code,
        string title,
        string svgContent,
        string zonesJson,
        Guid createdBy,
        string? specialtyCode = null,
        string? description = null,
        string? annotationStyleJson = null,
        string? linkedFormCodesJson = null,
        string? zoneBodySiteMapJson = null,
        Guid? organizationId = null,
        string version = "1.0.0",
        TimeProvider? timeProvider = null)
    {
        Validate(code, title, svgContent, zonesJson, version);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new DiagramDefinition
        {
            Id = Guid.CreateVersion7(now),
            Code = code,
            SpecialtyCode = specialtyCode,
            Version = version,
            Status = DiagramStatus.Draft,
            Title = title,
            Description = description,
            SvgContent = svgContent,
            ZonesJson = zonesJson,
            AnnotationStyleJson = annotationStyleJson ?? "{}",
            LinkedFormCodesJson = linkedFormCodesJson ?? "[]",
            ZoneBodySiteMapJson = zoneBodySiteMapJson ?? "{}",
            OrganizationId = organizationId,
            CreatedAt = now,
            CreatedBy = createdBy
        };
    }

    /// <summary>Edita una definición Draft. Lanza si está Published o Retired (inmutable).</summary>
    public void EditDraft(
        string title,
        string svgContent,
        string zonesJson,
        string? description,
        string? specialtyCode,
        string? annotationStyleJson,
        string? linkedFormCodesJson,
        string? zoneBodySiteMapJson)
    {
        if (Status != DiagramStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Solo se pueden editar definiciones en Draft. Estado actual: {Status}.");
        }
        Validate(Code, title, svgContent, zonesJson, Version);
        Title = title;
        SvgContent = svgContent;
        ZonesJson = zonesJson;
        Description = description;
        SpecialtyCode = specialtyCode;
        AnnotationStyleJson = annotationStyleJson ?? "{}";
        LinkedFormCodesJson = linkedFormCodesJson ?? "[]";
        ZoneBodySiteMapJson = zoneBodySiteMapJson ?? "{}";
    }

    /// <summary>Publica la definición. Inmutable a partir de acá.</summary>
    public void Publish(Guid publishedBy, TimeProvider? timeProvider = null)
    {
        if (Status != DiagramStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Solo se publican definiciones Draft. Estado actual: {Status}.");
        }
        Status = DiagramStatus.Published;
        PublishedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        PublishedBy = publishedBy;
    }

    /// <summary>Retira una definición publicada. Encounters previos siguen referenciándola para auditoría.</summary>
    public void Retire()
    {
        if (Status != DiagramStatus.Published)
        {
            throw new InvalidOperationException(
                $"Solo se retiran definiciones Published. Estado actual: {Status}.");
        }
        Status = DiagramStatus.Retired;
    }

    private static void Validate(string code, string title, string svgContent, string zonesJson, string version)
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
        if (string.IsNullOrWhiteSpace(svgContent))
        {
            throw new ArgumentException("SvgContent no puede estar vacío.", nameof(svgContent));
        }
        if (string.IsNullOrWhiteSpace(zonesJson) || zonesJson.Trim() == "[]")
        {
            throw new ArgumentException("ZonesJson no puede estar vacío (un diagrama sin zonas no es interactivo).", nameof(zonesJson));
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Version es obligatoria (usar SemVer ej. '1.0.0').", nameof(version));
        }
    }
}

public enum DiagramStatus
{
    Draft = 0,
    Published = 1,
    Retired = 2,
}
