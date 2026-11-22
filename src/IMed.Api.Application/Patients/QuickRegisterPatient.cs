using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Patients;

/// <param name="IdentifierType">Código del tipo de identificación del registro (p. ej. <c>IdentidadNacional</c>).</param>
/// <param name="Gender">FHIR <c>male</c>, <c>female</c>, <c>other</c> o <c>unknown</c>; opcional.</param>
public sealed record QuickRegisterPatientCommand(
    Guid OrganizationId,
    string CountryCode,
    string IdentifierType,
    string IdentifierValue,
    string GivenNames,
    string FamilyNames,
    DateOnly BirthDate,
    string? Gender = null,
    string? Phone = null);

/// <param name="Created">
/// <c>false</c> si ya había un paciente con esa identificación: se devuelve ese y no se crea otro.
/// </param>
public sealed record QuickRegisterPatientResult(string PatientFhirId, bool Created, string DisplayName);

/// <summary>
/// Alta rápida de un paciente desde un mostrador (la Caja, al vender). Solo la identificación:
/// tipo y número de documento, nombres, apellidos y fecha de nacimiento (UX-Cobro-Anticipado D6).
///
/// <para><b>Por qué un endpoint propio y no el alta del expediente.</b> El alta de pacientes va por
/// el proxy FHIR y exige <c>record.write</c>, que es permiso de escribir el expediente; el cajero no
/// lo tiene ni debe tenerlo. Este camino crea solo la identificación y nada clínico.</para>
///
/// <para><b>Sin duplicados, también bajo carrera.</b> El paciente se crea con un alta condicional
/// (<c>ifNoneExist</c> por el identificador): si otro mostrador lo registró un instante antes, HAPI
/// devuelve el existente en lugar de crear un segundo. Sin identificación no hay alta rápida: es lo
/// que hace inevitables los duplicados.</para>
/// </summary>
public sealed class QuickRegisterPatientHandler(
    IHapiFhirClient hapi,
    IIdentifierTypeRegistry registry,
    IIdentifierValidator validator,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> Generos = ["male", "female", "other", "unknown"];

    /// <exception cref="ArgumentException">Falta un dato obligatorio o la identificación no es válida.</exception>
    public async Task<QuickRegisterPatientResult> HandleAsync(QuickRegisterPatientCommand cmd, CancellationToken ct = default)
    {
        var tipo = registry.Find(cmd.CountryCode, cmd.IdentifierType)
            ?? throw new ArgumentException($"Tipo de identificación desconocido: {cmd.IdentifierType}.", nameof(cmd));
        var validacion = validator.Validate(cmd.CountryCode, cmd.IdentifierType, cmd.IdentifierValue ?? string.Empty);
        if (!validacion.Valid)
        {
            throw new ArgumentException(
                $"{tipo.Label} inválida: {string.Join(" ", validacion.Errors)}", nameof(cmd));
        }
        var numero = validacion.Normalized ?? cmd.IdentifierValue!.Trim();

        var nombres = (cmd.GivenNames ?? string.Empty).Trim();
        var apellidos = (cmd.FamilyNames ?? string.Empty).Trim();
        if (nombres.Length == 0 || apellidos.Length == 0)
        {
            throw new ArgumentException("Escribí los nombres y los apellidos del paciente.", nameof(cmd));
        }
        var hoy = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (cmd.BirthDate > hoy || cmd.BirthDate < hoy.AddYears(-130))
        {
            throw new ArgumentException("La fecha de nacimiento no es válida.", nameof(cmd));
        }
        var genero = string.IsNullOrWhiteSpace(cmd.Gender) ? null : cmd.Gender.Trim().ToLowerInvariant();
        if (genero is not null && !Generos.Contains(genero))
        {
            throw new ArgumentException("El sexo es male, female, other o unknown.", nameof(cmd));
        }

        var texto = $"{nombres} {apellidos}";
        var paciente = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["active"] = true,
            ["identifier"] = new JsonArray(new JsonObject
            {
                ["use"] = "official",
                ["system"] = tipo.FhirSystemUri,
                ["value"] = numero,
                ["type"] = new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject
                    {
                        ["system"] = "http://terminology.hl7.org/CodeSystem/v2-0203",
                        ["code"] = tipo.Hl7V2Code,
                        ["display"] = tipo.Label,
                    }),
                    ["text"] = tipo.Label,
                },
            }),
            ["name"] = new JsonArray(new JsonObject
            {
                ["use"] = "official",
                ["text"] = texto,
                ["family"] = apellidos,
                ["given"] = new JsonArray(nombres.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            }),
            ["birthDate"] = cmd.BirthDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (genero is not null)
        {
            paciente["gender"] = genero;
        }
        if (!string.IsNullOrWhiteSpace(cmd.Phone))
        {
            paciente["telecom"] = new JsonArray(new JsonObject
            {
                ["system"] = "phone", ["value"] = cmd.Phone.Trim(), ["use"] = "mobile",
            });
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["resource"] = paciente,
                ["request"] = new JsonObject
                {
                    ["method"] = "POST",
                    ["url"] = "Patient",
                    ["ifNoneExist"] = $"identifier={Uri.EscapeDataString($"{tipo.FhirSystemUri}|{numero}")}",
                },
            }),
        };

        var resultado = (await hapi.ExecuteTransactionAsync(bundle, $"org-{cmd.OrganizationId}", ct)).Single();
        var id = resultado.ResourceId
            ?? throw new InvalidOperationException("HAPI no devolvió el id del paciente.");
        var creado = resultado.Status.StartsWith("201", StringComparison.Ordinal);

        if (!creado)
        {
            // Ya existía: se devuelve con su nombre real, no con el que se escribió.
            var existente = await hapi.ReadResourceAsync("Patient", id, $"org-{cmd.OrganizationId}", ct);
            return new QuickRegisterPatientResult(id, false, NombreDe(existente) ?? texto);
        }
        return new QuickRegisterPatientResult(id, true, texto);
    }

    private static string? NombreDe(JsonObject? paciente)
    {
        var nombre = paciente?["name"]?.AsArray().FirstOrDefault();
        if (nombre is null)
        {
            return null;
        }
        var texto = nombre["text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(texto))
        {
            return texto;
        }
        var partes = (nombre["given"]?.AsArray().Select(g => g?.GetValue<string>()) ?? [])
            .Append(nombre["family"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x));
        var armado = string.Join(' ', partes);
        return armado.Length > 0 ? armado : null;
    }
}
