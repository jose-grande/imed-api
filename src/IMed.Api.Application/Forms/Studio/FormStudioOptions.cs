namespace IMed.Api.Application.Forms.Studio;

public sealed class FormStudioOptions
{
    public const string SectionName = "FormStudio";

    /// <summary>
    /// Base de la <c>url</c> de las extensiones FHIR que guardan los campos personalizados:
    /// <c>{ExtensionBaseUrl}/{nombre-del-campo}</c>. El valor por defecto es un marcador; cada
    /// despliegue debe fijar su dominio canónico.
    /// </summary>
    public string ExtensionBaseUrl { get; set; } = "https://imed.example/fhir/StructureDefinition/patient-custom-field";
}
