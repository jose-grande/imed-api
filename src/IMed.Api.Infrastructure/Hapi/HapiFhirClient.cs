using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Infrastructure.Keycloak;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Hapi;

/// <summary>
/// Cliente HTTP minimal contra HAPI FHIR R5. Tras Epica B, HAPI exige JWT
/// Bearer; este cliente lo presenta usando el service account compartido
/// (<see cref="KeycloakServiceAccountTokenProvider"/>).
/// </summary>
public sealed class HapiFhirClient(
    HttpClient httpClient,
    KeycloakServiceAccountTokenProvider tokenProvider,
    ILogger<HapiFhirClient> logger) : IHapiFhirClient
{
    private const string TenantPartitionHeader = "X-Tenant-Partition";

    /// <summary>
    /// Opciones de serialización para los payloads FHIR. FHIR rechaza
    /// propiedades con valor <c>null</c> (ej: <c>"id": null</c> en POST genera
    /// un 422 "This property must be a simple value, not a Null"); con
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> esos campos se omiten
    /// del wire en vez de emitirse como null.
    /// </summary>
    private static readonly JsonSerializerOptions FhirJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> CreatePartitionAsync(
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new ArgumentException("partitionName es obligatorio.", nameof(partitionName));
        }

        await EnsureBearerAsync(cancellationToken);

        // HAPI partition management operation. La id numerica la deriva HAPI;
        // pasamos un hash deterministico del nombre como sugerencia. El nombre es
        // lo que importa porque el interceptor resuelve por nombre.
        var partitionId = StableIdFromName(partitionName);

        var payload = new
        {
            resourceType = "Parameters",
            parameter = new object[]
            {
                new { name = "id", valueInteger = partitionId },
                new { name = "name", valueCode = partitionName },
                new { name = "description", valueString = $"IMed tenant partition for {partitionName}" }
            }
        };

        // Esta operacion afecta config global; va con allPartitions (sin header).
        using var resp = await httpClient.PostAsJsonAsync("$partition-management-create-partition", payload, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            // Si la particion ya existe, HAPI devuelve 400 con mensaje. Idempotente: lo tratamos como OK.
            if (body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Particion '{Name}' ya existe; continuando.", partitionName);
                return partitionName;
            }
            logger.LogError(
                "Fallo crear particion '{Name}'. Status={Status}, Body={Body}",
                partitionName, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        logger.LogInformation("Particion '{Name}' creada.", partitionName);
        return partitionName;
    }

    public async Task<string> CreateOrganizationAsync(
        CreateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildOrganizationPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Organization")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };

        // Si el caller especifica una particion (caso bootstrap), va al header
        // X-Tenant-Partition para que el interceptor escriba en esa particion
        // en vez de allPartitions/default.
        if (!string.IsNullOrWhiteSpace(request.PartitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "POST /fhir/Organization fallo. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var id = doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("HAPI devolvio un Organization sin `id`.");

        logger.LogInformation("Organization creada en HAPI con id={Id}, partition={Partition}", id, request.PartitionName ?? "(default)");
        return id;
    }

    public async Task<string> CreatePractitionerAsync(
        UpsertPractitionerRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildPractitionerPayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Practitioner")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "POST /fhir/Practitioner fallo. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var id = doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("HAPI devolvió un Practitioner sin `id`.");
        logger.LogInformation("Practitioner creado en HAPI id={Id}, partition={Partition}", id, request.PartitionName);
        return id;
    }

    public async Task UpdatePractitionerAsync(
        string practitionerFhirId,
        UpsertPractitionerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(practitionerFhirId))
        {
            throw new ArgumentException("practitionerFhirId es obligatorio.", nameof(practitionerFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildPractitionerPayload(request, idForPut: practitionerFhirId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"Practitioner/{practitionerFhirId}")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "PUT /fhir/Practitioner/{Id} fallo. Status={Status}, Body={Body}",
                practitionerFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        logger.LogInformation("Practitioner {Id} actualizado.", practitionerFhirId);
    }

    public async Task<PractitionerSnapshot?> GetPractitionerAsync(
        string practitionerFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(practitionerFhirId))
        {
            throw new ArgumentException("practitionerFhirId es obligatorio.", nameof(practitionerFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"Practitioner/{practitionerFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/Practitioner/{Id} fallo. Status={Status}, Body={Body}",
                practitionerFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParsePractitionerSnapshot(doc);
    }

    public async Task<string> CreatePractitionerRoleAsync(
        UpsertPractitionerRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildPractitionerRolePayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "PractitionerRole")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "POST /fhir/PractitionerRole fallo. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var id = doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("HAPI devolvió un PractitionerRole sin `id`.");
        logger.LogInformation("PractitionerRole creado en HAPI id={Id}, partition={Partition}", id, request.PartitionName);
        return id;
    }

    public async Task UpdatePractitionerRoleAsync(
        string practitionerRoleFhirId,
        UpsertPractitionerRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(practitionerRoleFhirId))
        {
            throw new ArgumentException("practitionerRoleFhirId es obligatorio.", nameof(practitionerRoleFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildPractitionerRolePayload(request, idForPut: practitionerRoleFhirId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"PractitionerRole/{practitionerRoleFhirId}")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "PUT /fhir/PractitionerRole/{Id} fallo. Status={Status}, Body={Body}",
                practitionerRoleFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        logger.LogInformation("PractitionerRole {Id} actualizado.", practitionerRoleFhirId);
    }

    public async Task<PractitionerRoleSnapshot?> GetPractitionerRoleAsync(
        string practitionerRoleFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(practitionerRoleFhirId))
        {
            throw new ArgumentException("practitionerRoleFhirId es obligatorio.", nameof(practitionerRoleFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"PractitionerRole/{practitionerRoleFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/PractitionerRole/{Id} fallo. Status={Status}, Body={Body}",
                practitionerRoleFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParsePractitionerRoleSnapshot(doc);
    }

    private async Task EnsureBearerAsync(CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static object BuildOrganizationPayload(CreateOrganizationRequest request)
    {
        var identifier = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.NationalTaxId))
        {
            identifier.Add(new
            {
                system = "https://imed.app/identifiers/hn/rtn",
                value = request.NationalTaxId
            });
        }

        return new
        {
            resourceType = "Organization",
            active = true,
            name = request.Name,
            identifier = identifier.Count > 0 ? identifier : null
        };
    }

    private const string LicenseSystem = "https://imed.app/identifiers/practitioner-license";

    private static object BuildPractitionerPayload(UpsertPractitionerRequest req, string? idForPut = null)
    {
        var identifier = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.LicenseNumber))
        {
            identifier.Add(new { system = LicenseSystem, value = req.LicenseNumber });
        }

        var telecom = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.Email))
        {
            telecom.Add(new { system = "email", value = req.Email });
        }
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            telecom.Add(new { system = "phone", value = req.Phone });
        }

        var name = new
        {
            family = req.Family,
            given = req.Given.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray(),
            prefix = string.IsNullOrWhiteSpace(req.Prefix) ? null : new[] { req.Prefix }
        };

        // FHIR R5 PUT requiere `id` en el body que matchee con la URL.
        return new
        {
            resourceType = "Practitioner",
            id = idForPut,
            active = req.Active,
            name = new[] { name },
            identifier = identifier.Count > 0 ? identifier : null,
            telecom = telecom.Count > 0 ? telecom : null,
            gender = string.IsNullOrWhiteSpace(req.Gender) ? null : req.Gender
        };
    }

    private static object BuildPractitionerRolePayload(UpsertPractitionerRoleRequest req, string? idForPut = null)
    {
        object[]? specialty = null;
        if (!string.IsNullOrWhiteSpace(req.SpecialtyCode))
        {
            specialty = [new
            {
                coding = new[]
                {
                    new
                    {
                        system = req.SpecialtySystem,
                        code = req.SpecialtyCode,
                        display = req.SpecialtyDisplay
                    }
                }
            }];
        }

        return new
        {
            resourceType = "PractitionerRole",
            id = idForPut,
            active = req.Active,
            practitioner = new { reference = $"Practitioner/{req.PractitionerFhirId}" },
            organization = new { reference = $"Organization/{req.OrganizationFhirId}" },
            specialty = specialty
        };
    }

    private static PractitionerSnapshot ParsePractitionerSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var active = !doc.TryGetProperty("active", out var activeEl) || activeEl.ValueKind != JsonValueKind.False;

        string? family = null;
        var given = new List<string>();
        string? prefix = null;
        if (doc.TryGetProperty("name", out var nameEl)
            && nameEl.ValueKind == JsonValueKind.Array
            && nameEl.GetArrayLength() > 0)
        {
            var first = nameEl[0];
            if (first.TryGetProperty("family", out var familyEl))
            {
                family = familyEl.GetString();
            }
            if (first.TryGetProperty("given", out var givenEl) && givenEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in givenEl.EnumerateArray())
                {
                    if (g.GetString() is { } s)
                    {
                        given.Add(s);
                    }
                }
            }
            if (first.TryGetProperty("prefix", out var prefixEl)
                && prefixEl.ValueKind == JsonValueKind.Array
                && prefixEl.GetArrayLength() > 0)
            {
                prefix = prefixEl[0].GetString();
            }
        }

        string? license = null;
        if (doc.TryGetProperty("identifier", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var ident in idsEl.EnumerateArray())
            {
                if (ident.TryGetProperty("system", out var sysEl)
                    && sysEl.GetString() == LicenseSystem
                    && ident.TryGetProperty("value", out var valEl))
                {
                    license = valEl.GetString();
                    break;
                }
            }
        }

        string? email = null;
        string? phone = null;
        if (doc.TryGetProperty("telecom", out var telEl) && telEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in telEl.EnumerateArray())
            {
                if (!t.TryGetProperty("system", out var sysEl)
                    || !t.TryGetProperty("value", out var valEl))
                {
                    continue;
                }
                var system = sysEl.GetString();
                if (system == "email" && email is null)
                {
                    email = valEl.GetString();
                }
                else if (system == "phone" && phone is null)
                {
                    phone = valEl.GetString();
                }
            }
        }

        string? gender = doc.TryGetProperty("gender", out var genderEl) ? genderEl.GetString() : null;

        return new PractitionerSnapshot(
            Id: id,
            Family: family,
            Given: given,
            Prefix: prefix,
            LicenseNumber: license,
            Email: email,
            Phone: phone,
            Gender: gender,
            Active: active);
    }

    private static PractitionerRoleSnapshot ParsePractitionerRoleSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var active = !doc.TryGetProperty("active", out var activeEl) || activeEl.ValueKind != JsonValueKind.False;

        string? code = null;
        string? display = null;
        string? system = null;
        if (doc.TryGetProperty("specialty", out var specEl)
            && specEl.ValueKind == JsonValueKind.Array
            && specEl.GetArrayLength() > 0)
        {
            var firstSpec = specEl[0];
            if (firstSpec.TryGetProperty("coding", out var codingEl)
                && codingEl.ValueKind == JsonValueKind.Array
                && codingEl.GetArrayLength() > 0)
            {
                var firstCoding = codingEl[0];
                if (firstCoding.TryGetProperty("code", out var codeEl))
                {
                    code = codeEl.GetString();
                }
                if (firstCoding.TryGetProperty("display", out var displayEl))
                {
                    display = displayEl.GetString();
                }
                if (firstCoding.TryGetProperty("system", out var systemEl))
                {
                    system = systemEl.GetString();
                }
            }
        }

        return new PractitionerRoleSnapshot(
            Id: id,
            SpecialtyCode: code,
            SpecialtyDisplay: display,
            SpecialtySystem: system,
            Active: active);
    }

    public async Task<IReadOnlyList<PractitionerRoleListItem>> SearchPractitionerRolesByOrganizationAsync(
        string organizationFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(organizationFhirId))
        {
            throw new ArgumentException("organizationFhirId es obligatorio.", nameof(organizationFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        // FHIR search: GET /PractitionerRole?organization=Organization/{id}&active=true
        // HAPI devuelve un Bundle (searchset). Iteramos entry[].resource.
        var url = $"PractitionerRole?organization=Organization/{Uri.EscapeDataString(organizationFhirId)}&active=true&_count=200";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/PractitionerRole?organization=... fallo. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!bundle.TryGetProperty("entry", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PractitionerRoleListItem>();
        }

        var items = new List<PractitionerRoleListItem>();
        foreach (var entry in entriesEl.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource))
            {
                continue;
            }
            var item = ParsePractitionerRoleListItem(resource);
            if (item is not null)
            {
                items.Add(item);
            }
        }
        return items;
    }

    private static PractitionerRoleListItem? ParsePractitionerRoleListItem(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        // practitioner.reference shape: "Practitioner/{fhirId}". Extraemos el id.
        string? practitionerFhirId = null;
        if (doc.TryGetProperty("practitioner", out var pracEl)
            && pracEl.TryGetProperty("reference", out var refEl)
            && refEl.GetString() is string refStr
            && refStr.StartsWith("Practitioner/", StringComparison.Ordinal))
        {
            practitionerFhirId = refStr["Practitioner/".Length..];
        }
        if (string.IsNullOrEmpty(practitionerFhirId))
        {
            // Sin Practitioner ref no podemos hidratar — skip.
            return null;
        }

        var active = !doc.TryGetProperty("active", out var activeEl) || activeEl.ValueKind != JsonValueKind.False;

        string? code = null;
        string? display = null;
        string? system = null;
        if (doc.TryGetProperty("specialty", out var specEl)
            && specEl.ValueKind == JsonValueKind.Array
            && specEl.GetArrayLength() > 0
            && specEl[0].TryGetProperty("coding", out var codingEl)
            && codingEl.ValueKind == JsonValueKind.Array
            && codingEl.GetArrayLength() > 0)
        {
            var firstCoding = codingEl[0];
            if (firstCoding.TryGetProperty("code", out var codeEl))
            {
                code = codeEl.GetString();
            }
            if (firstCoding.TryGetProperty("display", out var displayEl))
            {
                display = displayEl.GetString();
            }
            if (firstCoding.TryGetProperty("system", out var systemEl))
            {
                system = systemEl.GetString();
            }
        }

        return new PractitionerRoleListItem(
            Id: id,
            PractitionerFhirId: practitionerFhirId,
            SpecialtyCode: code,
            SpecialtyDisplay: display,
            SpecialtySystem: system,
            Active: active);
    }

    public async Task<IReadOnlyList<PatientMatch>> SearchPatientsByIdentifierAsync(
        string identifierSystem,
        string identifierValue,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifierSystem))
        {
            throw new ArgumentException("identifierSystem es obligatorio.", nameof(identifierSystem));
        }
        if (string.IsNullOrWhiteSpace(identifierValue))
        {
            throw new ArgumentException("identifierValue es obligatorio.", nameof(identifierValue));
        }
        await EnsureBearerAsync(cancellationToken);

        // FHIR search: GET /Patient?identifier=system|value
        // Escapa cualquier '|' literal en el valor con backslash (RFC 3986 + FHIR §3.1.1.5.1).
        var safeValue = identifierValue.Replace("|", "\\|", StringComparison.Ordinal);
        var token = $"{identifierSystem}|{safeValue}";
        var url = $"Patient?identifier={Uri.EscapeDataString(token)}&_count=20";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/Patient?identifier=... falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!bundle.TryGetProperty("entry", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PatientMatch>();
        }

        var matches = new List<PatientMatch>();
        foreach (var entry in entriesEl.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource))
            {
                continue;
            }
            var match = ParsePatientMatch(resource);
            if (match is not null)
            {
                matches.Add(match);
            }
        }
        return matches;
    }

    private static PatientMatch? ParsePatientMatch(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string? displayName = null;
        if (doc.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.Array && nameEl.GetArrayLength() > 0)
        {
            var first = nameEl[0];
            // Preferir name.text si está; sino armar given + family.
            if (first.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                displayName = textEl.GetString();
            }
            else
            {
                var parts = new List<string>();
                if (first.TryGetProperty("given", out var givenEl) && givenEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in givenEl.EnumerateArray())
                    {
                        var s = g.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            parts.Add(s);
                        }
                    }
                }
                if (first.TryGetProperty("family", out var famEl) && famEl.ValueKind == JsonValueKind.String)
                {
                    var s = famEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        parts.Add(s);
                    }
                }
                if (parts.Count > 0)
                {
                    displayName = string.Join(" ", parts);
                }
            }
        }

        string? birthDate = null;
        if (doc.TryGetProperty("birthDate", out var bdEl) && bdEl.ValueKind == JsonValueKind.String)
        {
            birthDate = bdEl.GetString();
        }

        return new PatientMatch(id, displayName, birthDate);
    }

    public async Task<PatientSnapshot?> GetPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("patientFhirId es obligatorio.", nameof(patientFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"Patient/{patientFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/Patient/{Id} falló. Status={Status}, Body={Body}",
                patientFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParsePatientSnapshot(doc);
    }

    private static PatientSnapshot? ParsePatientSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var match = ParsePatientMatch(doc);
        var displayName = match?.DisplayName;
        var birthDate = match?.BirthDate;

        string? gender = null;
        if (doc.TryGetProperty("gender", out var gEl) && gEl.ValueKind == JsonValueKind.String)
        {
            gender = gEl.GetString();
        }

        string? identifierLabel = null;
        string? identifierValue = null;
        if (doc.TryGetProperty("identifier", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            // Preferimos el primer identifier con use=official; si no hay, el primero.
            JsonElement? chosen = null;
            foreach (var ident in idsEl.EnumerateArray())
            {
                if (ident.TryGetProperty("use", out var useEl)
                    && useEl.ValueKind == JsonValueKind.String
                    && string.Equals(useEl.GetString(), "official", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = ident;
                    break;
                }
            }
            if (chosen is null && idsEl.GetArrayLength() > 0)
            {
                chosen = idsEl[0];
            }
            if (chosen is { } ident2)
            {
                if (ident2.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String)
                {
                    identifierValue = valEl.GetString();
                }
                if (ident2.TryGetProperty("type", out var typeEl))
                {
                    if (typeEl.TryGetProperty("text", out var typeTextEl) && typeTextEl.ValueKind == JsonValueKind.String)
                    {
                        identifierLabel = typeTextEl.GetString();
                    }
                    else if (typeEl.TryGetProperty("coding", out var codingEl)
                             && codingEl.ValueKind == JsonValueKind.Array
                             && codingEl.GetArrayLength() > 0
                             && codingEl[0].TryGetProperty("display", out var codingDispEl)
                             && codingDispEl.ValueKind == JsonValueKind.String)
                    {
                        identifierLabel = codingDispEl.GetString();
                    }
                }
            }
        }

        return new PatientSnapshot(
            Id: id,
            DisplayName: displayName,
            BirthDate: birthDate,
            Gender: gender,
            IdentifierLabel: identifierLabel,
            IdentifierValue: identifierValue);
    }

    public async Task<string> CreateRelatedPersonAsync(
        UpsertRelatedPersonRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildRelatedPersonPayload(request, idForPut: null);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "RelatedPerson")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        if (!string.IsNullOrWhiteSpace(request.PartitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);
        }
        httpRequest.Headers.Add("Accept", "application/fhir+json");

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "POST /fhir/RelatedPerson falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return created.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()!
            : throw new InvalidOperationException("HAPI no devolvió id al crear RelatedPerson.");
    }

    public async Task UpdateRelatedPersonAsync(
        string relatedPersonFhirId,
        UpsertRelatedPersonRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relatedPersonFhirId))
        {
            throw new ArgumentException("relatedPersonFhirId es obligatorio.", nameof(relatedPersonFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildRelatedPersonPayload(request, idForPut: relatedPersonFhirId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"RelatedPerson/{relatedPersonFhirId}")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        if (!string.IsNullOrWhiteSpace(request.PartitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);
        }
        httpRequest.Headers.Add("Accept", "application/fhir+json");

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "PUT /fhir/RelatedPerson/{Id} falló. Status={Status}, Body={Body}",
                relatedPersonFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<IReadOnlyList<RelatedPersonSnapshot>> SearchRelatedPersonsByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("patientFhirId es obligatorio.", nameof(patientFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var url = $"RelatedPerson?patient=Patient/{Uri.EscapeDataString(patientFhirId)}&active=true&_count=50";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/RelatedPerson?patient=... falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!bundle.TryGetProperty("entry", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RelatedPersonSnapshot>();
        }

        var items = new List<RelatedPersonSnapshot>();
        foreach (var entry in entriesEl.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource))
            {
                continue;
            }
            var snap = ParseRelatedPersonSnapshot(resource, patientFhirId);
            if (snap is not null)
            {
                items.Add(snap);
            }
        }
        return items;
    }

    private static Dictionary<string, object?> BuildRelatedPersonPayload(UpsertRelatedPersonRequest req, string? idForPut)
    {
        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "RelatedPerson",
            ["active"] = req.Active,
            ["patient"] = new { reference = $"Patient/{req.PatientFhirId}" }
        };

        if (idForPut is not null)
        {
            payload["id"] = idForPut;
        }

        // Relationship: CodeableConcept con coding del CodeSystem v3-RoleCode.
        // Ej. "PRN" = parent, "GUARD" = guardian (legal), "SPS" = spouse, "CHILD" = child.
        if (!string.IsNullOrWhiteSpace(req.Relationship))
        {
            payload["relationship"] = new object[]
            {
                new
                {
                    coding = new object[]
                    {
                        new
                        {
                            system = "http://terminology.hl7.org/CodeSystem/v3-RoleCode",
                            code = req.Relationship
                        }
                    }
                }
            };
        }

        if (!string.IsNullOrWhiteSpace(req.Family)
            || (req.Given is not null && req.Given.Count > 0))
        {
            var name = new Dictionary<string, object?> { ["use"] = "official" };
            if (!string.IsNullOrWhiteSpace(req.Family))
            {
                name["family"] = req.Family;
            }
            if (req.Given is not null && req.Given.Count > 0)
            {
                name["given"] = req.Given;
            }
            payload["name"] = new object[] { name };
        }

        if (!string.IsNullOrWhiteSpace(req.IdentifierValue))
        {
            var identifier = new Dictionary<string, object?>
            {
                ["use"] = "official",
                ["value"] = req.IdentifierValue!
            };
            if (!string.IsNullOrWhiteSpace(req.IdentifierSystem))
            {
                identifier["system"] = req.IdentifierSystem!;
            }
            if (!string.IsNullOrWhiteSpace(req.IdentifierTypeCode))
            {
                identifier["type"] = new
                {
                    coding = new object[]
                    {
                        new
                        {
                            system = req.IdentifierTypeSystem ?? "http://terminology.hl7.org/CodeSystem/v2-0203",
                            code = req.IdentifierTypeCode
                        }
                    }
                };
            }
            payload["identifier"] = new object[] { identifier };
        }

        var telecom = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            telecom.Add(new { system = "phone", value = req.Phone, use = "mobile" });
        }
        if (!string.IsNullOrWhiteSpace(req.Email))
        {
            telecom.Add(new { system = "email", value = req.Email });
        }
        if (telecom.Count > 0)
        {
            payload["telecom"] = telecom;
        }

        if (!string.IsNullOrWhiteSpace(req.Gender))
        {
            payload["gender"] = req.Gender;
        }
        if (!string.IsNullOrWhiteSpace(req.BirthDate))
        {
            payload["birthDate"] = req.BirthDate;
        }

        return payload;
    }

    private static RelatedPersonSnapshot? ParseRelatedPersonSnapshot(JsonElement doc, string patientFhirId)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string? relationship = null;
        if (doc.TryGetProperty("relationship", out var relEl)
            && relEl.ValueKind == JsonValueKind.Array
            && relEl.GetArrayLength() > 0
            && relEl[0].TryGetProperty("coding", out var codingEl)
            && codingEl.ValueKind == JsonValueKind.Array
            && codingEl.GetArrayLength() > 0
            && codingEl[0].TryGetProperty("code", out var codeEl))
        {
            relationship = codeEl.GetString();
        }

        string? displayName = null;
        if (doc.TryGetProperty("name", out var nameEl)
            && nameEl.ValueKind == JsonValueKind.Array
            && nameEl.GetArrayLength() > 0)
        {
            var first = nameEl[0];
            if (first.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                displayName = textEl.GetString();
            }
            else
            {
                var parts = new List<string>();
                if (first.TryGetProperty("given", out var givenEl) && givenEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in givenEl.EnumerateArray())
                    {
                        var s = g.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            parts.Add(s);
                        }
                    }
                }
                if (first.TryGetProperty("family", out var famEl) && famEl.ValueKind == JsonValueKind.String)
                {
                    var s = famEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        parts.Add(s);
                    }
                }
                if (parts.Count > 0)
                {
                    displayName = string.Join(" ", parts);
                }
            }
        }

        string? identifierSystem = null;
        string? identifierValue = null;
        if (doc.TryGetProperty("identifier", out var idArrEl)
            && idArrEl.ValueKind == JsonValueKind.Array
            && idArrEl.GetArrayLength() > 0)
        {
            var firstId = idArrEl[0];
            if (firstId.TryGetProperty("system", out var sysEl) && sysEl.ValueKind == JsonValueKind.String)
            {
                identifierSystem = sysEl.GetString();
            }
            if (firstId.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String)
            {
                identifierValue = valEl.GetString();
            }
        }

        string? phone = null;
        string? email = null;
        if (doc.TryGetProperty("telecom", out var teleEl) && teleEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in teleEl.EnumerateArray())
            {
                if (!t.TryGetProperty("system", out var sysEl) || sysEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                if (!t.TryGetProperty("value", out var valEl) || valEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var sysStr = sysEl.GetString();
                if (sysStr == "phone" && phone is null)
                {
                    phone = valEl.GetString();
                }
                else if (sysStr == "email" && email is null)
                {
                    email = valEl.GetString();
                }
            }
        }

        bool active = true;
        if (doc.TryGetProperty("active", out var activeEl) && activeEl.ValueKind == JsonValueKind.False)
        {
            active = false;
        }

        return new RelatedPersonSnapshot(
            Id: id,
            PatientFhirId: patientFhirId,
            Relationship: relationship,
            DisplayName: displayName,
            IdentifierSystem: identifierSystem,
            IdentifierValue: identifierValue,
            Phone: phone,
            Email: email,
            Active: active);
    }

    public async Task<string> CreateAllergyIntoleranceAsync(
        UpsertAllergyIntoleranceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) && string.IsNullOrWhiteSpace(request.CodeText))
        {
            throw new ArgumentException("Se requiere Code o CodeText.", nameof(request));
        }
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildAllergyIntolerancePayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "AllergyIntolerance")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        if (!string.IsNullOrWhiteSpace(request.PartitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);
        }
        httpRequest.Headers.Add("Accept", "application/fhir+json");

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "POST /fhir/AllergyIntolerance falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return created.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()!
            : throw new InvalidOperationException("HAPI no devolvió id al crear AllergyIntolerance.");
    }

    public async Task<IReadOnlyList<AllergyIntoleranceSnapshot>> SearchAllergyIntolerancesByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("patientFhirId es obligatorio.", nameof(patientFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        // clinical-status=active retorna solo allergies vigentes. Para historial
        // completo el caller puede llamar este método dos veces o agregar un
        // overload con flag — Hito 2 solo necesita activos.
        var url = $"AllergyIntolerance?patient=Patient/{Uri.EscapeDataString(patientFhirId)}&clinical-status=active&_count=100";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/AllergyIntolerance?patient=... falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!bundle.TryGetProperty("entry", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AllergyIntoleranceSnapshot>();
        }

        var items = new List<AllergyIntoleranceSnapshot>();
        foreach (var entry in entriesEl.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource))
            {
                continue;
            }
            var snap = ParseAllergyIntoleranceSnapshot(resource, patientFhirId);
            if (snap is not null)
            {
                items.Add(snap);
            }
        }
        return items;
    }

    private static Dictionary<string, object?> BuildAllergyIntolerancePayload(UpsertAllergyIntoleranceRequest req)
    {
        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "AllergyIntolerance",
            ["clinicalStatus"] = new
            {
                coding = new object[]
                {
                    new
                    {
                        system = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical",
                        code = "active",
                    }
                }
            },
            ["verificationStatus"] = new
            {
                coding = new object[]
                {
                    new
                    {
                        system = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification",
                        code = "confirmed",
                    }
                }
            },
            ["patient"] = new { reference = $"Patient/{req.PatientFhirId}" },
            ["recordedDate"] = DateTimeOffset.UtcNow.ToString("o"),
        };

        // code: si vino del catálogo, system SNOMED + code + display. Si vino
        // texto libre, solo .text. Ambos casos coexisten en el mismo CodeableConcept.
        var code = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            code["coding"] = new object[]
            {
                new
                {
                    system = "http://snomed.info/sct",
                    code = req.Code,
                    display = req.CodeText
                }
            };
        }
        if (!string.IsNullOrWhiteSpace(req.CodeText))
        {
            code["text"] = req.CodeText;
        }
        payload["code"] = code;

        if (!string.IsNullOrWhiteSpace(req.Category))
        {
            // FHIR value-set required: food | medication | environment | biologic.
            payload["category"] = new[] { req.Category };
        }
        if (!string.IsNullOrWhiteSpace(req.Severity))
        {
            // FHIR value-set required: mild | moderate | severe.
            payload["criticality"] = req.Severity;
        }
        if (!string.IsNullOrWhiteSpace(req.Note))
        {
            payload["note"] = new object[] { new { text = req.Note } };
        }

        return payload;
    }

    private static AllergyIntoleranceSnapshot? ParseAllergyIntoleranceSnapshot(JsonElement doc, string patientFhirId)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string? code = null;
        string? codeDisplay = null;
        if (TryGetObjectProperty(doc, "code", out var codeEl))
        {
            if (codeEl.TryGetProperty("coding", out var codingEl)
                && codingEl.ValueKind == JsonValueKind.Array
                && codingEl.GetArrayLength() > 0)
            {
                var first = codingEl[0];
                if (first.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    code = c.GetString();
                }
                if (first.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    codeDisplay = d.GetString();
                }
            }
            if (codeDisplay is null
                && codeEl.TryGetProperty("text", out var txt)
                && txt.ValueKind == JsonValueKind.String)
            {
                codeDisplay = txt.GetString();
            }
        }

        string? category = null;
        if (doc.TryGetProperty("category", out var catArr)
            && catArr.ValueKind == JsonValueKind.Array
            && catArr.GetArrayLength() > 0
            && catArr[0].ValueKind == JsonValueKind.String)
        {
            category = catArr[0].GetString();
        }

        string? severity = null;
        if (doc.TryGetProperty("criticality", out var sevEl) && sevEl.ValueKind == JsonValueKind.String)
        {
            severity = sevEl.GetString();
        }

        string? note = null;
        if (doc.TryGetProperty("note", out var noteEl)
            && noteEl.ValueKind == JsonValueKind.Array
            && noteEl.GetArrayLength() > 0
            && noteEl[0].TryGetProperty("text", out var noteText)
            && noteText.ValueKind == JsonValueKind.String)
        {
            note = noteText.GetString();
        }

        DateTimeOffset? recordedDate = null;
        if (doc.TryGetProperty("recordedDate", out var rd)
            && rd.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(rd.GetString(), out var parsed))
        {
            recordedDate = parsed;
        }

        bool active = true;
        if (doc.TryGetProperty("clinicalStatus", out var cs)
            && cs.TryGetProperty("coding", out var csCoding)
            && csCoding.ValueKind == JsonValueKind.Array
            && csCoding.GetArrayLength() > 0
            && csCoding[0].TryGetProperty("code", out var csCode)
            && csCode.ValueKind == JsonValueKind.String)
        {
            active = csCode.GetString() == "active";
        }

        return new AllergyIntoleranceSnapshot(
            Id: id,
            PatientFhirId: patientFhirId,
            Code: code,
            CodeDisplay: codeDisplay,
            Category: category,
            Severity: severity,
            Note: note,
            RecordedDate: recordedDate,
            Active: active);
    }

    // ──────────────────── MedicationRequest (Épica K) ────────────────────

    public async Task<string> CreateMedicationRequestAsync(
        UpsertMedicationRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AtcCode) && string.IsNullOrWhiteSpace(request.MedicationText))
        {
            throw new ArgumentException("Se requiere AtcCode o MedicationText.", nameof(request));
        }
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildMedicationRequestPayload(request, idForPut: null);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "MedicationRequest")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        if (!string.IsNullOrWhiteSpace(request.PartitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);
        }
        httpRequest.Headers.Add("Accept", "application/fhir+json");

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "POST /fhir/MedicationRequest falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return created.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()!
            : throw new InvalidOperationException("HAPI no devolvió id al crear MedicationRequest.");
    }

    public async Task UpdateMedicationRequestAsync(
        string medicationRequestFhirId,
        UpsertMedicationRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(medicationRequestFhirId))
        {
            throw new ArgumentException("medicationRequestFhirId es obligatorio.", nameof(medicationRequestFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildMedicationRequestPayload(request, idForPut: medicationRequestFhirId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"MedicationRequest/{medicationRequestFhirId}")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        if (!string.IsNullOrWhiteSpace(request.PartitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);
        }
        httpRequest.Headers.Add("Accept", "application/fhir+json");

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "PUT /fhir/MedicationRequest/{Id} falló. Status={Status}, Body={Body}",
                medicationRequestFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<MedicationRequestSnapshot?> GetMedicationRequestAsync(
        string medicationRequestFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(medicationRequestFhirId))
        {
            throw new ArgumentException("medicationRequestFhirId es obligatorio.", nameof(medicationRequestFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"MedicationRequest/{medicationRequestFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/MedicationRequest/{Id} falló. Status={Status}, Body={Body}",
                medicationRequestFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseMedicationRequestSnapshot(doc);
    }

    public async Task<IReadOnlyList<MedicationRequestSnapshot>> SearchMedicationRequestsByEncounterAsync(
        string encounterFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(encounterFhirId))
        {
            throw new ArgumentException("encounterFhirId es obligatorio.", nameof(encounterFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var url = $"MedicationRequest?encounter=Encounter/{Uri.EscapeDataString(encounterFhirId)}&_count=100";
        return await SearchMedicationRequestsAsync(url, partitionName, cancellationToken);
    }

    public async Task<IReadOnlyList<MedicationRequestSnapshot>> SearchMedicationRequestsByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("patientFhirId es obligatorio.", nameof(patientFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        // Excluimos draft/entered-in-error en el listado del paciente — esos
        // estados son "en proceso" o errados y no deberían aparecer en la
        // historia clínica. El form del encounter sí los necesita; usa
        // SearchByEncounter en su lugar.
        var url = $"MedicationRequest?patient=Patient/{Uri.EscapeDataString(patientFhirId)}"
                  + "&status=active,completed,stopped&_sort=-authoredon&_count=100";
        return await SearchMedicationRequestsAsync(url, partitionName, cancellationToken);
    }

    public async Task<IReadOnlyList<MedicationRequestSnapshot>> SearchMedicationRequestsByGroupAsync(
        string groupIdentifierValue,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupIdentifierValue))
        {
            throw new ArgumentException("groupIdentifierValue es obligatorio.", nameof(groupIdentifierValue));
        }
        await EnsureBearerAsync(cancellationToken);

        // Búsqueda por token de groupIdentifier (search param `group-identifier`).
        // Buscamos por valor sin system: los valores son UUID únicos.
        var url = $"MedicationRequest?group-identifier={Uri.EscapeDataString(groupIdentifierValue)}&_count=100";
        return await SearchMedicationRequestsAsync(url, partitionName, cancellationToken);
    }

    private async Task<IReadOnlyList<MedicationRequestSnapshot>> SearchMedicationRequestsAsync(
        string url, string partitionName, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/MedicationRequest?... falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!bundle.TryGetProperty("entry", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MedicationRequestSnapshot>();
        }

        var items = new List<MedicationRequestSnapshot>();
        foreach (var entry in entriesEl.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource))
            {
                continue;
            }
            var snap = ParseMedicationRequestSnapshot(resource);
            if (snap is not null)
            {
                items.Add(snap);
            }
        }
        return items;
    }

    private static Dictionary<string, object?> BuildMedicationRequestPayload(
        UpsertMedicationRequestRequest req, string? idForPut)
    {
        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "MedicationRequest",
            ["status"] = req.Status,
            ["intent"] = req.Intent,
            ["subject"] = new { reference = $"Patient/{req.PatientFhirId}" },
            ["authoredOn"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        if (idForPut is not null)
        {
            payload["id"] = idForPut;
        }
        if (!string.IsNullOrWhiteSpace(req.EncounterFhirId))
        {
            payload["encounter"] = new { reference = $"Encounter/{req.EncounterFhirId}" };
        }
        if (!string.IsNullOrWhiteSpace(req.RequesterPractitionerId))
        {
            payload["requester"] = new { reference = $"Practitioner/{req.RequesterPractitionerId}" };
        }

        // medication: CodeableReference (R5). Usamos .concept con coding ATC
        // si vino del catálogo, .text adicional si hay nombre comercial / libre.
        var concept = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(req.AtcCode))
        {
            concept["coding"] = new object[]
            {
                new
                {
                    system = "http://www.whocc.no/atc",
                    code = req.AtcCode,
                    display = req.MedicationDisplay
                }
            };
        }
        if (!string.IsNullOrWhiteSpace(req.MedicationText))
        {
            concept["text"] = req.MedicationText;
        }
        else if (!string.IsNullOrWhiteSpace(req.MedicationDisplay))
        {
            concept["text"] = req.MedicationDisplay;
        }
        payload["medication"] = new { concept = concept };

        // dosageInstruction: solo armamos el primer item con .text + .route
        // opcional. Hito 2 no necesita timing/frequency estructurados — el
        // texto del médico es suficiente para el PDF.
        var dosage = new Dictionary<string, object?>
        {
            ["text"] = req.DosageText,
        };
        if (!string.IsNullOrWhiteSpace(req.Route))
        {
            // Route como CodeableConcept con text — el código SNOMED por vía
            // queda como follow-up (no hay catálogo de rutas en piloto).
            dosage["route"] = new { text = req.Route };
        }
        payload["dosageInstruction"] = new object[] { dosage };

        if (req.DispenseQuantity.HasValue)
        {
            var quantity = new Dictionary<string, object?>
            {
                ["value"] = req.DispenseQuantity.Value,
            };
            if (!string.IsNullOrWhiteSpace(req.DispenseUnit))
            {
                quantity["unit"] = req.DispenseUnit;
            }
            payload["dispenseRequest"] = new { quantity };
        }

        if (!string.IsNullOrWhiteSpace(req.Note))
        {
            payload["note"] = new object[] { new { text = req.Note } };
        }

        return payload;
    }

    private static MedicationRequestSnapshot? ParseMedicationRequestSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string status = "unknown";
        if (doc.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String)
        {
            status = stEl.GetString() ?? "unknown";
        }
        string intent = "order";
        if (doc.TryGetProperty("intent", out var inEl) && inEl.ValueKind == JsonValueKind.String)
        {
            intent = inEl.GetString() ?? "order";
        }

        string patientFhirId = string.Empty;
        if (TryGetObjectProperty(doc, "subject", out var subjEl)
            && subjEl.TryGetProperty("reference", out var refEl)
            && refEl.ValueKind == JsonValueKind.String)
        {
            var r = refEl.GetString() ?? string.Empty;
            patientFhirId = r.StartsWith("Patient/", StringComparison.Ordinal) ? r[8..] : r;
        }

        string? encounterFhirId = null;
        if (TryGetObjectProperty(doc, "encounter", out var encEl)
            && encEl.TryGetProperty("reference", out var encRefEl)
            && encRefEl.ValueKind == JsonValueKind.String)
        {
            var r = encRefEl.GetString() ?? string.Empty;
            encounterFhirId = r.StartsWith("Encounter/", StringComparison.Ordinal) ? r[10..] : r;
        }

        string? requesterPractitionerId = null;
        if (TryGetObjectProperty(doc, "requester", out var reqEl)
            && reqEl.TryGetProperty("reference", out var reqRefEl)
            && reqRefEl.ValueKind == JsonValueKind.String)
        {
            var r = reqRefEl.GetString() ?? string.Empty;
            requesterPractitionerId = r.StartsWith("Practitioner/", StringComparison.Ordinal) ? r[13..] : r;
        }

        // medication: puede venir como CodeableReference (R5) con .concept,
        // o legacy como .medicationCodeableConcept. Soportamos ambos por
        // compatibilidad.
        string? medCode = null;
        string? medDisplay = null;
        string? medText = null;
        JsonElement? medConcept = null;
        if (doc.TryGetProperty("medication", out var mEl)
            && mEl.TryGetProperty("concept", out var conceptEl))
        {
            medConcept = conceptEl;
        }
        else if (doc.TryGetProperty("medicationCodeableConcept", out var legacyEl))
        {
            medConcept = legacyEl;
        }
        if (medConcept.HasValue)
        {
            var c = medConcept.Value;
            if (c.TryGetProperty("coding", out var codingEl)
                && codingEl.ValueKind == JsonValueKind.Array
                && codingEl.GetArrayLength() > 0)
            {
                var first = codingEl[0];
                if (first.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                {
                    medCode = codeEl.GetString();
                }
                if (first.TryGetProperty("display", out var displayEl) && displayEl.ValueKind == JsonValueKind.String)
                {
                    medDisplay = displayEl.GetString();
                }
            }
            if (c.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                medText = textEl.GetString();
            }
        }

        // dosage: tomamos el primer item.
        string? dosageText = null;
        string? route = null;
        string? patientInstruction = null;
        if (doc.TryGetProperty("dosageInstruction", out var dosArr)
            && dosArr.ValueKind == JsonValueKind.Array
            && dosArr.GetArrayLength() > 0)
        {
            var first = dosArr[0];
            if (first.TryGetProperty("text", out var dosTextEl) && dosTextEl.ValueKind == JsonValueKind.String)
            {
                dosageText = dosTextEl.GetString();
            }
            if (first.TryGetProperty("patientInstruction", out var pInstrEl) && pInstrEl.ValueKind == JsonValueKind.String)
            {
                patientInstruction = pInstrEl.GetString();
            }
            if (first.TryGetProperty("route", out var routeEl))
            {
                if (routeEl.TryGetProperty("text", out var routeTextEl) && routeTextEl.ValueKind == JsonValueKind.String)
                {
                    route = routeTextEl.GetString();
                }
                else if (routeEl.TryGetProperty("coding", out var routeCodingEl)
                         && routeCodingEl.ValueKind == JsonValueKind.Array
                         && routeCodingEl.GetArrayLength() > 0
                         && routeCodingEl[0].TryGetProperty("display", out var routeDispEl)
                         && routeDispEl.ValueKind == JsonValueKind.String)
                {
                    route = routeDispEl.GetString();
                }
            }
        }

        decimal? dispenseQuantity = null;
        string? dispenseUnit = null;
        if (doc.TryGetProperty("dispenseRequest", out var dispEl)
            && dispEl.TryGetProperty("quantity", out var qEl))
        {
            if (qEl.TryGetProperty("value", out var qValEl)
                && qValEl.TryGetDecimal(out var qVal))
            {
                dispenseQuantity = qVal;
            }
            if (qEl.TryGetProperty("unit", out var qUnitEl) && qUnitEl.ValueKind == JsonValueKind.String)
            {
                dispenseUnit = qUnitEl.GetString();
            }
        }

        string? note = null;
        if (doc.TryGetProperty("note", out var noteArr)
            && noteArr.ValueKind == JsonValueKind.Array
            && noteArr.GetArrayLength() > 0
            && noteArr[0].TryGetProperty("text", out var noteTextEl)
            && noteTextEl.ValueKind == JsonValueKind.String)
        {
            note = noteTextEl.GetString();
        }

        DateTimeOffset? authoredOn = null;
        if (doc.TryGetProperty("authoredOn", out var auEl)
            && auEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(auEl.GetString(), out var parsedDate))
        {
            authoredOn = parsedDate;
        }

        // groupIdentifier: agrupa los medicamentos de una misma receta.
        string? groupIdentifierValue = null;
        if (doc.TryGetProperty("groupIdentifier", out var gidEl)
            && gidEl.TryGetProperty("value", out var gidValEl)
            && gidValEl.ValueKind == JsonValueKind.String)
        {
            groupIdentifierValue = gidValEl.GetString();
        }

        return new MedicationRequestSnapshot(
            Id: id,
            PatientFhirId: patientFhirId,
            EncounterFhirId: encounterFhirId,
            Status: status,
            Intent: intent,
            MedicationCode: medCode,
            MedicationDisplay: medDisplay,
            MedicationText: medText,
            DosageText: dosageText,
            Route: route,
            DispenseQuantity: dispenseQuantity,
            DispenseUnit: dispenseUnit,
            Note: note,
            RequesterPractitionerId: requesterPractitionerId,
            AuthoredOn: authoredOn,
            GroupIdentifierValue: groupIdentifierValue,
            PatientInstruction: patientInstruction);
    }

    // ──────────────── ServiceRequest / Lab Orders (Épica L) ────────────────

    private const string LoincSystem = "http://loinc.org";
    // SNOMED CT 108252007 = "Laboratory procedure". El HL7 service-category
    // CodeSystem es otro slice (Adoption/Counselling/Education) que NO incluye
    // este código — usarlo allí provoca un 422 "Unknown code".
    private const string SnomedSystem = "http://snomed.info/sct";
    private const string LabCategoryCode = "108252007";
    private const string LabCategoryDisplay = "Laboratory procedure";

    public async Task<string> CreateLabOrderAsync(
        UpsertLabOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildLabOrderPayload(request, idForPut: null);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "ServiceRequest")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "POST /fhir/ServiceRequest falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var id = doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("HAPI devolvió un ServiceRequest sin `id`.");
        logger.LogInformation(
            "ServiceRequest (lab) creado en HAPI id={Id}, patient={PatientId}, partition={Partition}",
            id, request.PatientFhirId, request.PartitionName);
        return id;
    }

    public async Task UpdateLabOrderAsync(
        string serviceRequestFhirId,
        UpsertLabOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceRequestFhirId))
        {
            throw new ArgumentException("serviceRequestFhirId es obligatorio.", nameof(serviceRequestFhirId));
        }
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildLabOrderPayload(request, idForPut: serviceRequestFhirId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"ServiceRequest/{serviceRequestFhirId}")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        if (!string.IsNullOrWhiteSpace(request.PartitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "PUT /fhir/ServiceRequest/{Id} falló. Status={Status}, Body={Body}",
                serviceRequestFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<LabOrderSnapshot?> GetLabOrderAsync(
        string serviceRequestFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceRequestFhirId))
        {
            throw new ArgumentException("serviceRequestFhirId es obligatorio.", nameof(serviceRequestFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"ServiceRequest/{serviceRequestFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/ServiceRequest/{Id} falló. Status={Status}, Body={Body}",
                serviceRequestFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseLabOrderSnapshot(doc);
    }

    public async Task<IReadOnlyList<LabOrderSnapshot>> SearchLabOrdersByEncounterAsync(
        string encounterFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(encounterFhirId))
        {
            throw new ArgumentException("encounterFhirId es obligatorio.", nameof(encounterFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        // Filtramos por categoría para no traer órdenes de imagen/referidos
        // cuando esos casos de uso aparezcan (post-piloto).
        var url = $"ServiceRequest?encounter=Encounter/{Uri.EscapeDataString(encounterFhirId)}"
                  + $"&category={Uri.EscapeDataString(SnomedSystem + "|" + LabCategoryCode)}"
                  + "&_count=100";
        return await SearchLabOrdersAsync(url, partitionName, cancellationToken);
    }

    public async Task<IReadOnlyList<LabOrderSnapshot>> SearchLabOrdersByRequisitionAsync(
        string requisitionValue,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requisitionValue))
        {
            throw new ArgumentException("requisitionValue es obligatorio.", nameof(requisitionValue));
        }
        await EnsureBearerAsync(cancellationToken);

        // Token search por ServiceRequest.requisition (valor único por orden).
        var url = $"ServiceRequest?requisition={Uri.EscapeDataString(requisitionValue)}"
                  + $"&category={Uri.EscapeDataString(SnomedSystem + "|" + LabCategoryCode)}"
                  + "&_count=100";
        return await SearchLabOrdersAsync(url, partitionName, cancellationToken);
    }

    public async Task<IReadOnlyList<LabOrderSnapshot>> SearchLabOrdersByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("patientFhirId es obligatorio.", nameof(patientFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var url = $"ServiceRequest?patient=Patient/{Uri.EscapeDataString(patientFhirId)}"
                  + $"&category={Uri.EscapeDataString(SnomedSystem + "|" + LabCategoryCode)}"
                  + "&status=active,completed,on-hold"
                  + "&_sort=-authored"
                  + "&_count=100";
        return await SearchLabOrdersAsync(url, partitionName, cancellationToken);
    }

    public async Task<IReadOnlyList<LabOrderSnapshot>> SearchActiveLabOrdersAsync(
        string partitionName, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);
        var url = $"ServiceRequest?category={Uri.EscapeDataString(SnomedSystem + "|" + LabCategoryCode)}"
                  + "&status=active"
                  + "&_sort=-authored"
                  + "&_count=200";
        return await SearchLabOrdersAsync(url, partitionName, cancellationToken);
    }

    public async Task<IReadOnlyList<LabOrderSnapshot>> SearchDraftLabOrdersAsync(
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var url = $"ServiceRequest?category={Uri.EscapeDataString(SnomedSystem + "|" + LabCategoryCode)}"
                  + "&status=draft"
                  + "&_sort=authored"
                  + "&_count=200";
        return await SearchLabOrdersAsync(url, partitionName, cancellationToken);
    }

    public async Task<string?> GetEncounterStatusAsync(
        string encounterFhirId,
        string partitionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(encounterFhirId))
        {
            throw new ArgumentException("encounterFhirId es obligatorio.", nameof(encounterFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get, $"Encounter/{Uri.EscapeDataString(encounterFhirId)}?_elements=status");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return doc.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;
    }

    private async Task<IReadOnlyList<LabOrderSnapshot>> SearchLabOrdersAsync(
        string url, string partitionName, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }

        // HAPI reutiliza el resultado de una búsqueda idéntica durante 60 segundos
        // (`reuse_cached_search_results_millis`, su default). Estas búsquedas son
        // lectura-después-de-escritura y con esa caché devuelven un conjunto viejo.
        //
        // El síntoma real: al cerrar la consulta se buscan las órdenes del encuentro para
        // promoverlas de `draft` a `active`. La pantalla ya había hecho esa misma búsqueda al
        // abrirse —cuando todavía no había órdenes—, así que HAPI contestaba con aquel resultado
        // vacío y no había nada que promover. La orden quedaba en `draft`, el laboratorio nunca la
        // recibía, y el médico leía "Consulta cerrada". Cerrar rápido fallaba siempre; tardar más
        // de un minuto funcionaba, que es lo que lo hacía parecer intermitente.
        httpRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "GET /fhir/ServiceRequest?... falló. Status={Status}, Body={Body}",
                (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!bundle.TryGetProperty("entry", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<LabOrderSnapshot>();
        }

        var items = new List<LabOrderSnapshot>();
        foreach (var entry in entriesEl.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource))
            {
                continue;
            }
            var snap = ParseLabOrderSnapshot(resource);
            if (snap is not null)
            {
                items.Add(snap);
            }
        }
        return items;
    }

    // ===================== LIS-3: Specimen / Provenance / externo =====================

    private const string AccessionSystem = "http://imed.local/lab/accession";
    private const string ExternalPractitionerSystem = "http://imed.local/practitioner/external";

    public async Task<string> CreateSpecimenAsync(
        UpsertSpecimenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildSpecimenPayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Specimen")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("POST /fhir/Specimen falló. Status={Status}, Body={Body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var id = doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("HAPI devolvió un Specimen sin `id`.");
        logger.LogInformation("Specimen creado id={Id}, accession={Acc}, partition={P}",
            id, request.AccessionNumber, request.PartitionName);
        return id;
    }

    public async Task<SpecimenSnapshot?> GetSpecimenAsync(
        string specimenFhirId, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(specimenFhirId))
        {
            throw new ArgumentException("specimenFhirId es obligatorio.", nameof(specimenFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"Specimen/{specimenFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseSpecimenSnapshot(doc);
    }

    public async Task<IReadOnlyList<SpecimenSnapshot>> SearchSpecimensByPatientAsync(
        string patientFhirId, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("patientFhirId es obligatorio.", nameof(patientFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var url = $"Specimen?subject=Patient/{Uri.EscapeDataString(patientFhirId)}&_count=100";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var results = new List<SpecimenSnapshot>();
        if (bundle.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("resource", out var res))
                {
                    var snap = ParseSpecimenSnapshot(res);
                    if (snap is not null)
                    {
                        results.Add(snap);
                    }
                }
            }
        }
        return results;
    }

    public async Task<string> CreateProvenanceAsync(
        CreateProvenanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "Provenance",
            ["target"] = new object[] { new { reference = request.TargetReference } },
            ["recorded"] = request.Recorded,
            ["activity"] = new
            {
                coding = new object[]
                {
                    new
                    {
                        system = "http://terminology.hl7.org/CodeSystem/v3-DataOperation",
                        code = request.ActivityCode,
                        display = request.ActivityDisplay
                    }
                }
            },
            ["agent"] = new object[] { new { who = new { reference = request.AgentWhoReference } } },
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Provenance")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("POST /fhir/Provenance falló. Status={Status}, Body={Body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("HAPI devolvió un Provenance sin `id`.");
    }

    public async Task<string> CreateExternalPractitionerAsync(
        ExternalPractitionerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            throw new ArgumentException("FullName es obligatorio.", nameof(request));
        }
        await EnsureBearerAsync(cancellationToken);

        var identifiers = new List<object>
        {
            new { system = ExternalPractitionerSystem, value = ExternalSlug(request.FullName) }
        };
        if (!string.IsNullOrWhiteSpace(request.Identifier))
        {
            identifiers.Add(new { system = ExternalPractitionerSystem + "/id", value = request.Identifier.Trim() });
        }
        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "Practitioner",
            ["active"] = true,
            ["name"] = new object[] { new { text = request.FullName.Trim() } },
            ["identifier"] = identifiers.ToArray(),
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Practitioner")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("POST /fhir/Practitioner (externo) falló. Status={Status}, Body={Body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("HAPI devolvió un Practitioner sin `id`.");
    }

    public async Task<string?> FindExternalPractitionerByNameAsync(
        string fullName, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }
        await EnsureBearerAsync(cancellationToken);

        var token = $"{ExternalPractitionerSystem}|{ExternalSlug(fullName)}";
        var url = $"Practitioner?identifier={Uri.EscapeDataString(token)}&_count=1";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (bundle.TryGetProperty("entry", out var entries)
            && entries.ValueKind == JsonValueKind.Array
            && entries.GetArrayLength() > 0
            && entries[0].TryGetProperty("resource", out var res)
            && res.TryGetProperty("id", out var idEl))
        {
            return idEl.GetString();
        }
        return null;
    }

    private static string ExternalSlug(string name) =>
        string.Join(' ', name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static Dictionary<string, object?> BuildSpecimenPayload(UpsertSpecimenRequest req)
    {
        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "Specimen",
            ["status"] = req.Status,
            ["subject"] = new { reference = $"Patient/{req.PatientFhirId}" },
            ["receivedTime"] = req.ReceivedTime,
            ["accessionIdentifier"] = new { system = AccessionSystem, value = req.AccessionNumber },
        };
        // Specimen.request[] — la(s) orden(es) que cubre esta muestra (N:M, LIS-RD-03).
        var requestRefs = new List<object> { new { reference = $"ServiceRequest/{req.ServiceRequestFhirId}" } };
        if (req.AdditionalServiceRequestFhirIds is { Count: > 0 })
        {
            foreach (var extra in req.AdditionalServiceRequestFhirIds)
            {
                if (!string.IsNullOrWhiteSpace(extra) && !string.Equals(extra, req.ServiceRequestFhirId, StringComparison.Ordinal))
                {
                    requestRefs.Add(new { reference = $"ServiceRequest/{extra}" });
                }
            }
        }
        payload["request"] = requestRefs.ToArray();
        if (!string.IsNullOrWhiteSpace(req.TypeText))
        {
            payload["type"] = new { text = req.TypeText };
        }
        var collection = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(req.CollectedDateTime))
        {
            collection["collectedDateTime"] = req.CollectedDateTime;
        }
        if (req.FastingHours is int h)
        {
            collection["fastingStatusDuration"] = new
            {
                value = h,
                unit = "h",
                system = "http://unitsofmeasure.org",
                code = "h"
            };
        }
        if (!string.IsNullOrWhiteSpace(req.CollectorPractitionerId))
        {
            collection["collector"] = new { reference = $"Practitioner/{req.CollectorPractitionerId}" };
        }
        if (!string.IsNullOrWhiteSpace(req.CollectionMethod))
        {
            collection["method"] = new { text = req.CollectionMethod };
        }
        if (!string.IsNullOrWhiteSpace(req.CollectionBodySite))
        {
            // R5: Specimen.collection.bodySite es CodeableReference → texto en .concept.
            collection["bodySite"] = new { concept = new { text = req.CollectionBodySite } };
        }
        if (collection.Count > 0)
        {
            payload["collection"] = collection;
        }
        if (!string.IsNullOrWhiteSpace(req.ParentSpecimenFhirId))
        {
            payload["parent"] = new object[] { new { reference = $"Specimen/{req.ParentSpecimenFhirId}" } };
        }
        if (!string.IsNullOrWhiteSpace(req.Note))
        {
            payload["note"] = new object[] { new { text = req.Note } };
        }
        return payload;
    }

    private static SpecimenSnapshot? ParseSpecimenSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var status = doc.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? "available" : "available";

        string patientFhirId = string.Empty;
        if (doc.TryGetProperty("subject", out var subj)
            && subj.TryGetProperty("reference", out var subjRef)
            && subjRef.ValueKind == JsonValueKind.String)
        {
            var r = subjRef.GetString() ?? string.Empty;
            patientFhirId = r.StartsWith("Patient/", StringComparison.Ordinal) ? r[8..] : r;
        }

        string? typeText = null;
        if (doc.TryGetProperty("type", out var ty) && ty.TryGetProperty("text", out var tyText))
        {
            typeText = tyText.GetString();
        }

        string? accession = null;
        if (doc.TryGetProperty("accessionIdentifier", out var acc) && acc.TryGetProperty("value", out var accVal))
        {
            accession = accVal.GetString();
        }

        string? receivedTime = doc.TryGetProperty("receivedTime", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString() : null;

        string? collectedDateTime = null;
        if (doc.TryGetProperty("collection", out var col) && col.TryGetProperty("collectedDateTime", out var cdt))
        {
            collectedDateTime = cdt.GetString();
        }

        string? serviceRequestFhirId = null;
        if (doc.TryGetProperty("request", out var reqArr)
            && reqArr.ValueKind == JsonValueKind.Array
            && reqArr.GetArrayLength() > 0
            && reqArr[0].TryGetProperty("reference", out var reqRef)
            && reqRef.ValueKind == JsonValueKind.String)
        {
            var r = reqRef.GetString() ?? string.Empty;
            serviceRequestFhirId = r.StartsWith("ServiceRequest/", StringComparison.Ordinal) ? r[15..] : r;
        }

        string? note = null;
        if (doc.TryGetProperty("note", out var noteArr)
            && noteArr.ValueKind == JsonValueKind.Array
            && noteArr.GetArrayLength() > 0
            && noteArr[0].TryGetProperty("text", out var noteText))
        {
            note = noteText.GetString();
        }

        return new SpecimenSnapshot(id, patientFhirId, status, typeText, accession, receivedTime, collectedDateTime, serviceRequestFhirId, note);
    }

    // ===================== LIS-4: resultados =====================

    private const string ObservationCategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string InterpretationSystem = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";
    private const string UcumSystem = "http://unitsofmeasure.org";

    public async Task<string> CreateObservationAsync(
        UpsertObservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        var payload = BuildObservationPayload(request, idForPut: null);
        return await PostFhirResourceAsync("Observation", payload, request.PartitionName, cancellationToken);
    }

    public async Task UpdateObservationAsync(
        string observationFhirId, UpsertObservationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(observationFhirId))
        {
            throw new ArgumentException("observationFhirId es obligatorio.", nameof(observationFhirId));
        }
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        // PUT sobre el mismo id: HAPI versiona el recurso, así que el valor anterior queda
        // en `_history` y la corrección es auditable sin tabla propia.
        var payload = BuildObservationPayload(request, idForPut: observationFhirId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"Observation/{observationFhirId}")
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, request.PartitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "PUT /fhir/Observation/{Id} falló. Status={Status}, Body={Body}",
                observationFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        logger.LogInformation("Observation {Id} actualizada.", observationFhirId);
    }

    private static Dictionary<string, object?> BuildObservationPayload(
        UpsertObservationRequest request, string? idForPut)
    {
        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "Observation",
            ["status"] = request.Status,
            ["category"] = new object[]
            {
                new { coding = new object[] { new { system = ObservationCategorySystem, code = "laboratory" } } }
            },
            ["code"] = BuildCodeableConcept(request.LoincCode, request.CodeText),
            ["subject"] = new { reference = $"Patient/{request.PatientFhirId}" },
            ["effectiveDateTime"] = request.EffectiveDateTime,
            ["issued"] = request.EffectiveDateTime,
        };
        if (!string.IsNullOrWhiteSpace(idForPut))
        {
            payload["id"] = idForPut;
        }
        if (!string.IsNullOrWhiteSpace(request.ServiceRequestFhirId))
        {
            payload["basedOn"] = new object[] { new { reference = $"ServiceRequest/{request.ServiceRequestFhirId}" } };
        }
        if (request.ValueNumeric is { } num)
        {
            payload["valueQuantity"] = new
            {
                value = num,
                unit = request.ValueUnit,
                system = UcumSystem,
                code = request.ValueUnit
            };
        }
        else if (!string.IsNullOrWhiteSpace(request.ValueString))
        {
            payload["valueString"] = request.ValueString;
        }
        if (!string.IsNullOrWhiteSpace(request.InterpretationCode))
        {
            payload["interpretation"] = new object[]
            {
                new { coding = new object[] { new { system = InterpretationSystem, code = request.InterpretationCode } } }
            };
        }
        var hasRange = request.ReferenceLow is not null || request.ReferenceHigh is not null
                       || !string.IsNullOrWhiteSpace(request.AppliesToSex)
                       || request.AppliesToAgeMin is not null || request.AppliesToAgeMax is not null;
        if (hasRange)
        {
            var range = new Dictionary<string, object?>();
            if (request.ReferenceLow is { } lo)
            {
                range["low"] = new { value = lo, unit = request.ValueUnit, system = UcumSystem, code = request.ValueUnit };
            }
            if (request.ReferenceHigh is { } hi)
            {
                range["high"] = new { value = hi, unit = request.ValueUnit, system = UcumSystem, code = request.ValueUnit };
            }
            // R2: población del rango — appliesTo (sexo) + age (Range en años).
            if (!string.IsNullOrWhiteSpace(request.AppliesToSex))
            {
                range["appliesTo"] = new object[] { new { text = request.AppliesToSex } };
            }
            if (request.AppliesToAgeMin is not null || request.AppliesToAgeMax is not null)
            {
                var age = new Dictionary<string, object?>();
                if (request.AppliesToAgeMin is { } amin)
                {
                    age["low"] = new { value = amin, unit = "a", system = UcumSystem, code = "a" };
                }
                if (request.AppliesToAgeMax is { } amax)
                {
                    age["high"] = new { value = amax, unit = "a", system = UcumSystem, code = "a" };
                }
                range["age"] = age;
            }
            payload["referenceRange"] = new object[] { range };
        }
        if (!string.IsNullOrWhiteSpace(request.SpecimenFhirId))
        {
            payload["specimen"] = new { reference = $"Specimen/{request.SpecimenFhirId}" };
        }
        if (!string.IsNullOrWhiteSpace(request.PerformerReference))
        {
            payload["performer"] = new object[] { new { reference = request.PerformerReference } };
        }
        // R2: agrupación de panel — Observation grouper (value-less) → componentes.
        if (request.HasMemberObservationIds is { Count: > 0 })
        {
            payload["hasMember"] = request.HasMemberObservationIds
                .Select(id => new { reference = $"Observation/{id}" })
                .ToArray();
        }
        // R2: reflejo (R5 Observation.triggeredBy).
        if (!string.IsNullOrWhiteSpace(request.TriggeredByObservationId))
        {
            payload["triggeredBy"] = new object[]
            {
                new
                {
                    observation = new { reference = $"Observation/{request.TriggeredByObservationId}" },
                    type = request.TriggeredByType ?? "reflex"
                }
            };
        }

        return payload;
    }

    public async Task<string> CreateDiagnosticReportAsync(
        UpsertDiagnosticReportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureBearerAsync(cancellationToken);

        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "DiagnosticReport",
            ["status"] = request.Status,
            ["category"] = new object[]
            {
                new { coding = new object[] { new { system = "http://terminology.hl7.org/CodeSystem/v2-0074", code = "LAB" } } }
            },
            ["code"] = BuildCodeableConcept(request.LoincCode, request.CodeText),
            ["subject"] = new { reference = $"Patient/{request.PatientFhirId}" },
            ["basedOn"] = new object[] { new { reference = $"ServiceRequest/{request.ServiceRequestFhirId}" } },
            ["effectiveDateTime"] = request.EffectiveDateTime,
            ["issued"] = request.EffectiveDateTime,
            ["result"] = request.ResultObservationIds
                .Select(id => (object)new { reference = $"Observation/{id}" }).ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(request.SpecimenFhirId))
        {
            payload["specimen"] = new object[] { new { reference = $"Specimen/{request.SpecimenFhirId}" } };
        }
        if (!string.IsNullOrWhiteSpace(request.PerformerReference))
        {
            payload["performer"] = new object[] { new { reference = request.PerformerReference } };
        }
        else if (!string.IsNullOrWhiteSpace(request.PerformerDisplay))
        {
            payload["performer"] = new object[] { new { display = request.PerformerDisplay } };
        }
        if (!string.IsNullOrWhiteSpace(request.Conclusion))
        {
            payload["conclusion"] = request.Conclusion;
        }

        return await PostFhirResourceAsync("DiagnosticReport", payload, request.PartitionName, cancellationToken);
    }

    public async Task<DiagnosticReportSnapshot?> GetDiagnosticReportAsync(
        string diagnosticReportFhirId, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(diagnosticReportFhirId))
        {
            throw new ArgumentException("diagnosticReportFhirId es obligatorio.", nameof(diagnosticReportFhirId));
        }
        await EnsureBearerAsync(cancellationToken);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"DiagnosticReport/{diagnosticReportFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseDiagnosticReportSnapshot(doc);
    }

    public async Task<IReadOnlyList<DiagnosticReportSnapshot>> SearchDiagnosticReportsByPatientAsync(
        string patientFhirId, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("patientFhirId es obligatorio.", nameof(patientFhirId));
        }
        await EnsureBearerAsync(cancellationToken);
        var url = $"DiagnosticReport?subject=Patient/{Uri.EscapeDataString(patientFhirId)}&_sort=-issued&_count=100";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var results = new List<DiagnosticReportSnapshot>();
        if (bundle.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("resource", out var res))
                {
                    var snap = ParseDiagnosticReportSnapshot(res);
                    if (snap is not null)
                    {
                        results.Add(snap);
                    }
                }
            }
        }
        return results;
    }

    public async Task<ObservationSnapshot?> GetObservationAsync(
        string observationFhirId, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(observationFhirId))
        {
            throw new ArgumentException("observationFhirId es obligatorio.", nameof(observationFhirId));
        }
        await EnsureBearerAsync(cancellationToken);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"Observation/{observationFhirId}");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseObservationSnapshot(doc);
    }

    private static string StripRef(JsonElement el, string prefix)
    {
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
        return s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : s;
    }

    private static (string? Code, string? Text) ParseCodeableConcept(JsonElement codeEl)
    {
        string? code = null, text = null;
        if (codeEl.TryGetProperty("coding", out var coding)
            && coding.ValueKind == JsonValueKind.Array && coding.GetArrayLength() > 0
            && coding[0].TryGetProperty("code", out var c))
        {
            code = c.GetString();
        }
        if (codeEl.TryGetProperty("text", out var t))
        {
            text = t.GetString();
        }
        return (code, text);
    }

    private static DiagnosticReportSnapshot? ParseDiagnosticReportSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        var status = doc.TryGetProperty("status", out var st) ? st.GetString() ?? "unknown" : "unknown";
        var patient = doc.TryGetProperty("subject", out var subj) && subj.TryGetProperty("reference", out var sr)
            ? StripRef(sr, "Patient/") : string.Empty;
        string? loinc = null, codeText = null;
        if (doc.TryGetProperty("code", out var code))
        {
            (loinc, codeText) = ParseCodeableConcept(code);
        }
        var issued = doc.TryGetProperty("issued", out var iss) && iss.ValueKind == JsonValueKind.String ? iss.GetString() : null;
        var conclusion = doc.TryGetProperty("conclusion", out var con) && con.ValueKind == JsonValueKind.String ? con.GetString() : null;

        string? performerDisplay = null;
        if (doc.TryGetProperty("performer", out var perf) && perf.ValueKind == JsonValueKind.Array && perf.GetArrayLength() > 0
            && perf[0].TryGetProperty("display", out var pd))
        {
            performerDisplay = pd.GetString();
        }
        string? specimenId = null;
        if (doc.TryGetProperty("specimen", out var spec) && spec.ValueKind == JsonValueKind.Array && spec.GetArrayLength() > 0
            && spec[0].TryGetProperty("reference", out var spr))
        {
            specimenId = StripRef(spr, "Specimen/");
        }
        string? srId = null;
        if (doc.TryGetProperty("basedOn", out var bo) && bo.ValueKind == JsonValueKind.Array && bo.GetArrayLength() > 0
            && bo[0].TryGetProperty("reference", out var bor))
        {
            srId = StripRef(bor, "ServiceRequest/");
        }
        var resultIds = new List<string>();
        if (doc.TryGetProperty("result", out var resArr) && resArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in resArr.EnumerateArray())
            {
                if (r.TryGetProperty("reference", out var rr))
                {
                    resultIds.Add(StripRef(rr, "Observation/"));
                }
            }
        }
        return new DiagnosticReportSnapshot(id, patient, status, loinc, codeText, issued, conclusion, performerDisplay, specimenId, srId, resultIds);
    }

    private static ObservationSnapshot? ParseObservationSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        var status = doc.TryGetProperty("status", out var st) ? st.GetString() ?? "unknown" : "unknown";
        var patient = doc.TryGetProperty("subject", out var subj) && subj.TryGetProperty("reference", out var sr)
            ? StripRef(sr, "Patient/") : string.Empty;
        string? loinc = null, codeText = null;
        if (doc.TryGetProperty("code", out var code))
        {
            (loinc, codeText) = ParseCodeableConcept(code);
        }
        decimal? valueNumeric = null;
        string? valueUnit = null, valueString = null;
        if (doc.TryGetProperty("valueQuantity", out var vq))
        {
            if (vq.TryGetProperty("value", out var v) && v.TryGetDecimal(out var dv))
            {
                valueNumeric = dv;
            }
            if (vq.TryGetProperty("unit", out var u))
            {
                valueUnit = u.GetString();
            }
        }
        else if (doc.TryGetProperty("valueString", out var vs))
        {
            valueString = vs.GetString();
        }
        string? flag = null;
        if (doc.TryGetProperty("interpretation", out var interp) && interp.ValueKind == JsonValueKind.Array && interp.GetArrayLength() > 0
            && interp[0].TryGetProperty("coding", out var ic) && ic.ValueKind == JsonValueKind.Array && ic.GetArrayLength() > 0
            && ic[0].TryGetProperty("code", out var icc))
        {
            flag = icc.GetString();
        }
        decimal? low = null, high = null;
        if (doc.TryGetProperty("referenceRange", out var rr) && rr.ValueKind == JsonValueKind.Array && rr.GetArrayLength() > 0)
        {
            var range = rr[0];
            if (range.TryGetProperty("low", out var lo) && lo.TryGetProperty("value", out var lov) && lov.TryGetDecimal(out var lod))
            {
                low = lod;
            }
            if (range.TryGetProperty("high", out var hi) && hi.TryGetProperty("value", out var hiv) && hiv.TryGetDecimal(out var hid))
            {
                high = hid;
            }
        }
        var effective = doc.TryGetProperty("effectiveDateTime", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString() : null;

        // Vínculos a preservar si el recurso se reescribe (corrección de resultado, LIS-4b).
        string? serviceRequestId = null;
        if (doc.TryGetProperty("basedOn", out var basedOn) && basedOn.ValueKind == JsonValueKind.Array
            && basedOn.GetArrayLength() > 0 && basedOn[0].TryGetProperty("reference", out var boRef))
        {
            serviceRequestId = StripRef(boRef, "ServiceRequest/");
        }
        string? specimenId = null;
        if (doc.TryGetProperty("specimen", out var specimen) && specimen.TryGetProperty("reference", out var spRef))
        {
            specimenId = StripRef(spRef, "Specimen/");
        }
        string? performerRef = null;
        if (doc.TryGetProperty("performer", out var performer) && performer.ValueKind == JsonValueKind.Array
            && performer.GetArrayLength() > 0 && performer[0].TryGetProperty("reference", out var pfRef)
            && pfRef.ValueKind == JsonValueKind.String)
        {
            performerRef = pfRef.GetString();
        }

        return new ObservationSnapshot(
            id, patient, status, loinc, codeText, valueNumeric, valueUnit, valueString, flag, low, high, effective,
            serviceRequestId, specimenId, performerRef);
    }

    public async Task<decimal?> GetLatestNumericObservationAsync(
        string patientFhirId, string loincCode, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId) || string.IsNullOrWhiteSpace(loincCode))
        {
            return null;
        }
        await EnsureBearerAsync(cancellationToken);
        var codeToken = $"{LoincSystem}|{loincCode}";
        var url = $"Observation?subject=Patient/{Uri.EscapeDataString(patientFhirId)}"
                  + $"&code={Uri.EscapeDataString(codeToken)}&_sort=-date&_count=1";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (bundle.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0
            && entries[0].TryGetProperty("resource", out var res))
        {
            var snap = ParseObservationSnapshot(res);
            return snap?.ValueNumeric;
        }
        return null;
    }

    public async Task PatchResourceStatusAsync(
        string resourceType, string resourceFhirId, string status, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceFhirId))
        {
            throw new ArgumentException("resourceFhirId es obligatorio.", nameof(resourceFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        var patch = $"[{{\"op\":\"replace\",\"path\":\"/status\",\"value\":\"{status}\"}}]";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, $"{resourceType}/{resourceFhirId}")
        {
            Content = new StringContent(patch, System.Text.Encoding.UTF8, "application/json-patch+json")
        };
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("PATCH /fhir/{Resource}/{Id} (status) falló. Status={Status}, Body={Body}",
                resourceType, resourceFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task PatchDiagnosticReportConclusionAsync(
        string reportFhirId, string conclusion, string partitionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reportFhirId))
        {
            throw new ArgumentException("reportFhirId es obligatorio.", nameof(reportFhirId));
        }
        await EnsureBearerAsync(cancellationToken);

        // "add" y no "replace": el informe puede no traer conclusion todavía, y en JSON Patch
        // "add" sobre un miembro existente lo reemplaza igual. "replace" fallaría si no existe.
        var value = JsonSerializer.Serialize(conclusion);
        var patch = $"[{{\"op\":\"add\",\"path\":\"/conclusion\",\"value\":{value}}}]";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, $"DiagnosticReport/{reportFhirId}")
        {
            Content = new StringContent(patch, System.Text.Encoding.UTF8, "application/json-patch+json")
        };
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("PATCH /fhir/DiagnosticReport/{Id} (conclusion) falló. Status={Status}, Body={Body}",
                reportFhirId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<IReadOnlyList<DiagnosticReportSnapshot>> SearchPreliminaryReportsAsync(
        string partitionName, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "DiagnosticReport?status=preliminary&_sort=-issued&_count=100");
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var results = new List<DiagnosticReportSnapshot>();
        if (bundle.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("resource", out var res))
                {
                    var snap = ParseDiagnosticReportSnapshot(res);
                    if (snap is not null)
                    {
                        results.Add(snap);
                    }
                }
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<ObservationHistoryPoint>> GetObservationHistoryAsync(
        string patientFhirId, string loincCode, string partitionName, int count, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId) || string.IsNullOrWhiteSpace(loincCode))
        {
            return [];
        }
        await EnsureBearerAsync(cancellationToken);
        var codeToken = $"{LoincSystem}|{loincCode}";
        var take = Math.Clamp(count, 1, 50);
        var url = $"Observation?subject=Patient/{Uri.EscapeDataString(patientFhirId)}"
                  + $"&code={Uri.EscapeDataString(codeToken)}&_sort=-date&_count={take}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(partitionName))
        {
            httpRequest.Headers.Add(TenantPartitionHeader, partitionName);
        }
        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        var bundle = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var points = new List<ObservationHistoryPoint>();
        if (bundle.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("resource", out var res))
                {
                    var snap = ParseObservationSnapshot(res);
                    if (snap?.ValueNumeric is { } v)
                    {
                        points.Add(new ObservationHistoryPoint(v, snap.EffectiveDateTime, snap.FlagCode));
                    }
                }
            }
        }
        return points;
    }

    private static object BuildCodeableConcept(string? loinc, string text)
    {
        if (!string.IsNullOrWhiteSpace(loinc))
        {
            return new
            {
                coding = new object[] { new { system = LoincSystem, code = loinc, display = text } },
                text
            };
        }
        return new { text };
    }

    private async Task<string> PostFhirResourceAsync(
        string resourceType, Dictionary<string, object?> payload, string partitionName, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, resourceType)
        {
            Content = JsonContent.Create(payload, options: FhirJson)
        };
        httpRequest.Headers.Add(TenantPartitionHeader, partitionName);

        using var resp = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("POST /fhir/{Resource} falló. Status={Status}, Body={Body}", resourceType, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return doc.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"HAPI devolvió un {resourceType} sin `id`.");
    }

    private static Dictionary<string, object?> BuildLabOrderPayload(
        UpsertLabOrderRequest req, string? idForPut)
    {
        var payload = new Dictionary<string, object?>
        {
            ["resourceType"] = "ServiceRequest",
            ["status"] = req.Status,
            ["intent"] = req.Intent,
            ["subject"] = new { reference = $"Patient/{req.PatientFhirId}" },
            ["authoredOn"] = DateTimeOffset.UtcNow.ToString("o"),
            ["category"] = new object[]
            {
                new
                {
                    coding = new object[]
                    {
                        new
                        {
                            system = SnomedSystem,
                            code = LabCategoryCode,
                            display = LabCategoryDisplay
                        }
                    },
                    text = "Laboratorio"
                }
            },
        };
        if (idForPut is not null)
        {
            payload["id"] = idForPut;
        }
        if (!string.IsNullOrWhiteSpace(req.EncounterFhirId))
        {
            payload["encounter"] = new { reference = $"Encounter/{req.EncounterFhirId}" };
        }
        if (!string.IsNullOrWhiteSpace(req.RequesterPractitionerId))
        {
            payload["requester"] = new { reference = $"Practitioner/{req.RequesterPractitionerId}" };
        }
        // R3/LIS-RD-02: requisición de la visita (agrupa las órdenes en recepción).
        if (!string.IsNullOrWhiteSpace(req.Requisition))
        {
            payload["requisition"] = new { system = "http://imed.local/requisition", value = req.Requisition };
        }

        // R5: ServiceRequest.code es CodeableReference (no CodeableConcept como
        // en R4). HAPI rechaza coding/text en la raíz — hay que envolver en
        // .concept (mismo cambio que sufrió MedicationRequest.medication).
        // Dual coding (LIS-RD-22): LOINC primero (si existe) + código local del panel.
        var coding = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.LoincCode))
        {
            coding.Add(new { system = LoincSystem, code = req.LoincCode, display = req.CodeDisplay ?? req.CodeText });
        }
        if (!string.IsNullOrWhiteSpace(req.LocalCode) && !string.IsNullOrWhiteSpace(req.LocalSystem))
        {
            coding.Add(new { system = req.LocalSystem, code = req.LocalCode, display = req.CodeText });
        }
        var concept = new Dictionary<string, object?> { ["text"] = req.CodeText };
        if (coding.Count > 0)
        {
            concept["coding"] = coding.ToArray();
        }
        payload["code"] = new { concept };

        if (!string.IsNullOrWhiteSpace(req.Priority))
        {
            payload["priority"] = req.Priority;
        }
        if (!string.IsNullOrWhiteSpace(req.OccurrenceDateTime))
        {
            payload["occurrenceDateTime"] = req.OccurrenceDateTime;
        }
        if (!string.IsNullOrWhiteSpace(req.ReasonText))
        {
            // R5 ServiceRequest.reason es CodeableReference[]. Para texto libre
            // emitimos solo .concept.text — sin coding (Condition queda como
            // follow-up).
            payload["reason"] = new object[]
            {
                new { concept = new { text = req.ReasonText } }
            };
        }
        if (!string.IsNullOrWhiteSpace(req.Note))
        {
            payload["note"] = new object[] { new { text = req.Note } };
        }

        return payload;
    }

    /// <summary>
    /// Accede a una propiedad que se espera objeto, tolerando que venga como <c>null</c> explícito.
    ///
    /// <para>Necesario porque <c>JsonElement.TryGetProperty</c> <b>lanza</b> (no devuelve false) si
    /// se lo invoca sobre un elemento nulo. Un emisor que serializa <c>"encounter": null</c> en vez
    /// de omitir la propiedad tumbaba el parseo de la orden entera — y con ella el endpoint.</para>
    /// </summary>
    private static bool TryGetObjectProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Object)
        {
            value = el;
            return true;
        }
        value = default;
        return false;
    }

    private static LabOrderSnapshot? ParseLabOrderSnapshot(JsonElement doc)
    {
        var id = doc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string status = "unknown";
        if (doc.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String)
        {
            status = stEl.GetString() ?? "unknown";
        }
        string intent = "order";
        if (doc.TryGetProperty("intent", out var inEl) && inEl.ValueKind == JsonValueKind.String)
        {
            intent = inEl.GetString() ?? "order";
        }

        string patientFhirId = string.Empty;
        if (doc.TryGetProperty("subject", out var subjEl)
            && subjEl.TryGetProperty("reference", out var refEl)
            && refEl.ValueKind == JsonValueKind.String)
        {
            var r = refEl.GetString() ?? string.Empty;
            patientFhirId = r.StartsWith("Patient/", StringComparison.Ordinal) ? r[8..] : r;
        }

        string? encounterFhirId = null;
        if (doc.TryGetProperty("encounter", out var encEl)
            && encEl.TryGetProperty("reference", out var encRefEl)
            && encRefEl.ValueKind == JsonValueKind.String)
        {
            var r = encRefEl.GetString() ?? string.Empty;
            encounterFhirId = r.StartsWith("Encounter/", StringComparison.Ordinal) ? r[10..] : r;
        }

        string? requesterPractitionerId = null;
        if (doc.TryGetProperty("requester", out var reqEl)
            && reqEl.TryGetProperty("reference", out var reqRefEl)
            && reqRefEl.ValueKind == JsonValueKind.String)
        {
            var r = reqRefEl.GetString() ?? string.Empty;
            requesterPractitionerId = r.StartsWith("Practitioner/", StringComparison.Ordinal) ? r[13..] : r;
        }

        string? loincCode = null;
        string? localCode = null;
        string? localSystem = null;
        string? codeDisplay = null;
        string? codeText = null;
        if (doc.TryGetProperty("code", out var codeEl))
        {
            // R5: code es CodeableReference — coding/text van adentro de .concept.
            // Soportamos también el shape legacy (coding/text en la raíz) para
            // que recursos viejos no rompan el listado.
            JsonElement? conceptEl = null;
            if (TryGetObjectProperty(codeEl, "concept", out var c5))
            {
                conceptEl = c5;
            }
            else if (codeEl.ValueKind == JsonValueKind.Object
                     && (codeEl.TryGetProperty("coding", out _) || codeEl.TryGetProperty("text", out _)))
            {
                conceptEl = codeEl;
            }

            if (conceptEl is { } concept)
            {
                if (concept.TryGetProperty("coding", out var codingEl)
                    && codingEl.ValueKind == JsonValueKind.Array)
                {
                    // Se reparte por `system`, no por posición. Las órdenes llevan coding dual
                    // —LOINC si el examen lo tiene, más el código local del panel—, y tomar
                    // `coding[0]` como LOINC hacía que un panel sin LOINC volviera etiquetado como
                    // estándar: el guardado siguiente escribía `CBC` bajo `http://loinc.org`.
                    foreach (var coding in codingEl.EnumerateArray())
                    {
                        var system = coding.TryGetProperty("system", out var sysEl)
                            && sysEl.ValueKind == JsonValueKind.String ? sysEl.GetString() : null;
                        var value = coding.TryGetProperty("code", out var c)
                            && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        var display = coding.TryGetProperty("display", out var d)
                            && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

                        if (string.Equals(system, LoincSystem, StringComparison.OrdinalIgnoreCase))
                        {
                            loincCode = value;
                            codeDisplay ??= display;
                        }
                        else
                        {
                            // Cualquier sistema que no sea LOINC es código propio de la clínica.
                            localCode ??= value;
                            localSystem ??= system;
                            codeDisplay ??= display;
                        }
                    }
                }
                if (concept.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String)
                {
                    codeText = txtEl.GetString();
                }
            }
        }

        string? priority = null;
        if (doc.TryGetProperty("priority", out var prEl) && prEl.ValueKind == JsonValueKind.String)
        {
            priority = prEl.GetString();
        }

        string? occurrenceDateTime = null;
        if (doc.TryGetProperty("occurrenceDateTime", out var ocEl) && ocEl.ValueKind == JsonValueKind.String)
        {
            occurrenceDateTime = ocEl.GetString();
        }

        string? reasonText = null;
        if (doc.TryGetProperty("reason", out var reasonArr)
            && reasonArr.ValueKind == JsonValueKind.Array
            && reasonArr.GetArrayLength() > 0)
        {
            var first = reasonArr[0];
            if (first.TryGetProperty("concept", out var conceptEl))
            {
                if (conceptEl.TryGetProperty("text", out var rtEl) && rtEl.ValueKind == JsonValueKind.String)
                {
                    reasonText = rtEl.GetString();
                }
            }
        }

        string? note = null;
        if (doc.TryGetProperty("note", out var noteArr)
            && noteArr.ValueKind == JsonValueKind.Array
            && noteArr.GetArrayLength() > 0
            && noteArr[0].TryGetProperty("text", out var ntEl)
            && ntEl.ValueKind == JsonValueKind.String)
        {
            note = ntEl.GetString();
        }

        DateTimeOffset? authoredOn = null;
        if (doc.TryGetProperty("authoredOn", out var auEl)
            && auEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(auEl.GetString(), out var parsedDate))
        {
            authoredOn = parsedDate;
        }

        // requisition: agrupa los exámenes/paneles de una misma orden de laboratorio.
        // El chequeo de `Object` no es decorativo: un emisor puede mandar `"requisition": null`
        // explícito (en vez de omitir la propiedad), y `TryGetProperty` sobre un elemento nulo
        // lanza en vez de devolver false — tumbando el parseo de toda la orden.
        string? requisition = null;
        if (TryGetObjectProperty(doc, "requisition", out var reqIdEl)
            && reqIdEl.TryGetProperty("value", out var reqValEl)
            && reqValEl.ValueKind == JsonValueKind.String)
        {
            requisition = reqValEl.GetString();
        }

        return new LabOrderSnapshot(
            Id: id,
            PatientFhirId: patientFhirId,
            EncounterFhirId: encounterFhirId,
            Status: status,
            Intent: intent,
            LoincCode: loincCode,
            CodeDisplay: codeDisplay,
            CodeText: codeText,
            Priority: priority,
            OccurrenceDateTime: occurrenceDateTime,
            ReasonText: reasonText,
            Note: note,
            RequesterPractitionerId: requesterPractitionerId,
            AuthoredOn: authoredOn,
            Requisition: requisition,
            LocalCode: localCode,
            LocalSystem: localSystem);
    }

    /// <summary>
    /// Deriva un id numerico estable a partir del nombre de la particion.
    /// HAPI requiere un id integer al crear; usamos un hash positivo del nombre
    /// para que el mismo nombre siempre produzca el mismo id.
    /// </summary>
    private static int StableIdFromName(string name)
    {
        unchecked
        {
            int hash = 23;
            foreach (var c in name)
            {
                hash = hash * 31 + c;
            }
            return Math.Abs(hash) + 1; // partition 0 esta reservada (DEFAULT)
        }
    }
}
