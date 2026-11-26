using System.Globalization;
using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Billing.Catalog;

namespace IMed.Api.Application.Agenda;

/// <summary>Quién pregunta: organización, usuario y sus permisos efectivos.</summary>
public sealed record AgendaCaller(
    Guid OrganizationId, Guid UserId, EffectivePermissions Permissions, string? PractitionerFhirId)
{
    /// <summary>Lectura amplia del expediente: ve motivo y notas de la cita, y todas las sedes.</summary>
    public bool ReadsRecord => Permissions.Has(PermissionCatalog.RecordReadAny);

    /// <summary>Ve la agenda de todas las sedes: quien lee todo el expediente o administra las sedes.</summary>
    public bool AllLocations => ReadsRecord || Permissions.Has(PermissionCatalog.OrgLocationsManage);
}

/// <summary>La sede pedida no es de las del usuario.</summary>
public sealed class AgendaScopeException(string message) : Exception(message);

public sealed record AgendaQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? LocationId,
    string? PractitionerFhirId,
    IReadOnlyList<string> Statuses);

/// <summary>Paciente tal como lo ve la agenda: identificación y contacto, nada clínico.</summary>
public sealed record AgendaPatientDto(
    string Id, string DisplayName, string? IdentifierLabel, string? IdentifierValue, string? Phone);

public sealed record AgendaPractitionerDto(string Id, string DisplayName);

/// <summary>
/// Una cita de la agenda. <see cref="Description"/> y <see cref="Note"/> son el motivo y las notas
/// de la cita: pueden tener información clínica, así que solo van a quien lee el expediente.
/// </summary>
public sealed record AgendaAppointmentDto(
    string Id,
    string? VersionId,
    string Status,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    int? MinutesDuration,
    string? ConsultationType,
    string? LocationFhirId,
    AgendaPatientDto? Patient,
    AgendaPractitionerDto? Practitioner,
    string? InProgressEncounterId,
    string? Description,
    string? Note);

/// <summary>
/// La agenda de la organización para quien la mantiene (<c>agenda.read</c>), sin abrirle el
/// expediente.
///
/// <list type="bullet">
///   <item><b>Solo pacientes con cita</b> en el rango pedido: lo que se ve es la agenda, no el
///   padrón. Abrir <c>Patient/{id}</c> a recepción habría dejado recorrer el padrón por id.</item>
///   <item><b>Del paciente, lo justo</b> para confirmar o llamar: nombre, identificación y
///   teléfono.</item>
///   <item><b>Sedes del usuario</b>; todas solo para quien lee el expediente o administra sedes.
///   Una cita sin sede (anterior a FAC-9) solo la ve ese mismo grupo.</item>
///   <item><b>Todo resuelto en el servidor</b>: antes el portal pedía cada paciente y cada médico
///   por separado, y un solo 403 dejaba todas las filas como "Paciente".</item>
/// </list>
/// </summary>
public sealed class ListAgendaHandler(IHapiFhirClient hapi, AgendaScope scope)
{
    /// <summary>Tope de citas por consulta. Una agenda de un día de una clínica cabe holgada.</summary>
    public const int MaxAppointments = 500;

    private static readonly string[] EstadosConEncuentro = ["arrived", "checked-in"];

    public async Task<IReadOnlyList<AgendaAppointmentDto>> HandleAsync(
        AgendaCaller caller, AgendaQuery query, CancellationToken ct = default)
    {
        if (query.To <= query.From)
        {
            throw new ArgumentException("El rango de fechas está invertido.", nameof(query));
        }
        var sedes = await scope.AllowedLocationsAsync(caller, query.LocationId, ct);
        var partition = $"org-{caller.OrganizationId}";

        var q = $"date=ge{Uri.EscapeDataString(query.From.ToString("o", CultureInfo.InvariantCulture))}"
            + $"&date=lt{Uri.EscapeDataString(query.To.ToString("o", CultureInfo.InvariantCulture))}"
            + $"&_sort=-date&_count={MaxAppointments}";
        if (!string.IsNullOrWhiteSpace(query.PractitionerFhirId))
        {
            q += $"&actor={Uri.EscapeDataString($"Practitioner/{query.PractitionerFhirId.Trim()}")}";
        }
        if (query.Statuses.Count > 0)
        {
            q += $"&status={Uri.EscapeDataString(string.Join(',', query.Statuses))}";
        }

        var citas = (await hapi.SearchResourcesAsync("Appointment", q, partition, ct))
            .Where(c => AgendaScope.Visible(sedes, LocationRef(c)))
            .ToList();

        // Pacientes y médicos: cada uno una vez, en paralelo y acotado.
        var pacientes = await ResolveAsync(
            citas.Select(PatientId).OfType<string>(), id => ReadPatientAsync(id, partition, ct));
        var medicos = await ResolveAsync(
            citas.Select(PractitionerId).OfType<string>(), id => ReadPractitionerAsync(id, partition, ct));

        var resultado = new List<AgendaAppointmentDto>(citas.Count);
        foreach (var c in citas)
        {
            var id = (string?)c["id"];
            if (id is null)
            {
                continue;
            }
            var status = (string?)c["status"] ?? "booked";
            string? encuentro = null;
            if (EstadosConEncuentro.Contains(status))
            {
                var enc = await hapi.SearchResourcesAsync(
                    "Encounter", $"appointment={Uri.EscapeDataString($"Appointment/{id}")}&status=in-progress", partition, ct);
                encuentro = enc.Count > 0 ? (string?)enc[0]["id"] : null;
            }
            var pid = PatientId(c);
            var did = PractitionerId(c);
            resultado.Add(new AgendaAppointmentDto(
                Id: id,
                VersionId: (string?)c["meta"]?["versionId"],
                Status: status,
                Start: ParseInstant((string?)c["start"]),
                End: ParseInstant((string?)c["end"]),
                MinutesDuration: c["minutesDuration"] is JsonValue m && m.TryGetValue<int>(out var mins) ? mins : null,
                ConsultationType: ConsultationTypeOf(c),
                LocationFhirId: LocationRef(c)?["Location/".Length..],
                Patient: pid is null ? null : pacientes.GetValueOrDefault(pid) ?? new AgendaPatientDto(pid, "Paciente", null, null, null),
                Practitioner: did is null ? null : medicos.GetValueOrDefault(did) ?? new AgendaPractitionerDto(did, "Médico"),
                InProgressEncounterId: encuentro,
                Description: caller.ReadsRecord ? (string?)c["description"] : null,
                Note: caller.ReadsRecord ? (string?)c["note"]?[0]?["text"] : null));
        }
        return resultado;
    }

    private static async Task<Dictionary<string, T>> ResolveAsync<T>(IEnumerable<string> ids, Func<string, Task<T?>> read)
        where T : class
    {
        using var gate = new SemaphoreSlim(8);
        var tareas = ids.Distinct(StringComparer.Ordinal).Select(async id =>
        {
            await gate.WaitAsync();
            try
            {
                return (id, value: await read(id));
            }
            finally
            {
                gate.Release();
            }
        });
        return (await Task.WhenAll(tareas))
            .Where(x => x.value is not null)
            .ToDictionary(x => x.id, x => x.value!, StringComparer.Ordinal);
    }

    private async Task<AgendaPatientDto?> ReadPatientAsync(string id, string partition, CancellationToken ct)
    {
        try
        {
            var p = await hapi.ReadResourceAsync("Patient", id, partition, ct);
            return p is null ? null : AgendaPatient.From(id, p);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<AgendaPractitionerDto?> ReadPractitionerAsync(string id, string partition, CancellationToken ct)
    {
        try
        {
            var p = await hapi.GetPractitionerAsync(id, partition, ct);
            if (p is null)
            {
                return null;
            }
            var nombre = string.Join(' ', new[] { p.Prefix }.Concat(p.Given).Append(p.Family)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            return new AgendaPractitionerDto(id, string.IsNullOrWhiteSpace(nombre) ? "Médico" : nombre);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal static string? PatientId(JsonObject c) =>
        RefId((string?)c["subject"]?["reference"], "Patient/") ?? ActorId(c, "Patient/");

    internal static string? PractitionerId(JsonObject c) => ActorId(c, "Practitioner/");

    internal static string? LocationRef(JsonObject c) =>
        (c["participant"] as JsonArray)?
            .Select(p => (string?)p?["actor"]?["reference"])
            .FirstOrDefault(r => r?.StartsWith("Location/", StringComparison.Ordinal) == true);

    private static string? ActorId(JsonObject c, string prefix) =>
        (c["participant"] as JsonArray)?
            .Select(p => RefId((string?)p?["actor"]?["reference"], prefix))
            .FirstOrDefault(id => id is not null);

    private static string? RefId(string? reference, string prefix) =>
        reference is not null && reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : null;

    private static string? ConsultationTypeOf(JsonObject c) =>
        (c["appointmentType"]?["coding"] as JsonArray)?
            .FirstOrDefault(x => (string?)x?["system"] == ConsultationTypeCodes.System)?["code"]?.GetValue<string>();

    private static DateTimeOffset? ParseInstant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>Identificación del paciente desde el recurso FHIR, sin nada más.</summary>
internal static class AgendaPatient
{
    public static AgendaPatientDto From(string id, JsonObject p)
    {
        var name = (p["name"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var texto = (string?)name?["text"];
        if (string.IsNullOrWhiteSpace(texto))
        {
            var given = (name?["given"] as JsonArray)?.Select(g => (string?)g).Where(g => !string.IsNullOrWhiteSpace(g)) ?? [];
            texto = string.Join(' ', given.Append((string?)name?["family"]).Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        var ident = (p["identifier"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var etiqueta = (string?)ident?["type"]?["text"] ?? (string?)ident?["type"]?["coding"]?[0]?["display"];
        var telefono = (p["telecom"] as JsonArray)?
            .Select(t => t as JsonObject)
            .FirstOrDefault(t => (string?)t?["system"] == "phone")?["value"]?.GetValue<string>();

        return new AgendaPatientDto(
            id,
            string.IsNullOrWhiteSpace(texto) ? "Paciente" : texto.Trim(),
            etiqueta,
            (string?)ident?["value"],
            telefono);
    }
}

/// <summary>Las sedes cuya agenda puede ver el usuario.</summary>
public sealed class AgendaScope(ILocationRepository locations)
{
    /// <summary>
    /// Ids de las sedes permitidas, o null si son todas. Con <paramref name="requested"/> devuelve
    /// solo esa, y falla si no es del usuario.
    /// </summary>
    public async Task<IReadOnlySet<Guid>?> AllowedLocationsAsync(
        AgendaCaller caller, Guid? requested, CancellationToken ct)
    {
        if (caller.AllLocations)
        {
            return requested is { } r ? new HashSet<Guid> { r } : null;
        }
        var propias = (await locations.ListForUserAsync(caller.OrganizationId, caller.UserId, ct))
            .Select(x => x.Location.Id)
            .ToHashSet();
        if (requested is { } pedida)
        {
            if (!propias.Contains(pedida))
            {
                throw new AgendaScopeException("No tenés asignada esa sede.");
            }
            return new HashSet<Guid> { pedida };
        }
        return propias;
    }

    /// <summary>¿Se ve una cita con esta referencia de sede?</summary>
    public static bool Visible(IReadOnlySet<Guid>? sedes, string? locationRef)
    {
        if (sedes is null)
        {
            return true;
        }
        return locationRef is not null
            && FhirLocationGuard.ParseSedeId(locationRef) is { } id
            && sedes.Contains(id);
    }
}

/// <summary>Estados a los que se mueve una cita desde la agenda.</summary>
public static class AgendaTransitions
{
    public static readonly IReadOnlySet<string> Targets =
        new HashSet<string>(StringComparer.Ordinal) { "arrived", "cancelled", "noshow" };

    /// <summary>Una cita cerrada no vuelve a moverse desde la agenda.</summary>
    public static readonly IReadOnlySet<string> Closed =
        new HashSet<string>(StringComparer.Ordinal) { "fulfilled", "cancelled", "noshow", "entered-in-error" };
}

/// <summary>
/// Cambia solo el <c>status</c> de una cita (llegó, cancelada, no se presentó), por JSON Patch.
///
/// <para>Antes el portal reescribía la cita entera con el estado nuevo. Con la agenda sin motivo
/// ni notas eso los habría borrado; y aun con la cita completa, dos personas moviendo la misma
/// cita se pisaban.</para>
/// </summary>
public sealed class ChangeAppointmentStatusHandler(IHapiFhirClient hapi, AgendaScope scope)
{
    public async Task<bool> HandleAsync(AgendaCaller caller, string appointmentId, string status, CancellationToken ct = default)
    {
        if (!AgendaTransitions.Targets.Contains(status))
        {
            throw new ArgumentException($"Estado no admitido desde la agenda: '{status}'.", nameof(status));
        }
        var partition = $"org-{caller.OrganizationId}";
        var cita = await hapi.ReadResourceAsync("Appointment", appointmentId, partition, ct);
        if (cita is null)
        {
            return false;
        }
        var sedes = await scope.AllowedLocationsAsync(caller, null, ct);
        if (!AgendaScope.Visible(sedes, ListAgendaHandler.LocationRef(cita)))
        {
            throw new AgendaScopeException("La cita es de una sede que no tenés asignada.");
        }
        var actual = (string?)cita["status"] ?? "booked";
        if (actual == status)
        {
            return true;
        }
        if (AgendaTransitions.Closed.Contains(actual))
        {
            throw new InvalidOperationException("La cita ya está cerrada y no se puede cambiar desde la agenda.");
        }
        await hapi.PatchResourceStatusAsync("Appointment", appointmentId, status, partition, ct);

        // Cancelada o no vino: si pasó por preclínica, su encuentro quedó `planned` y se cancela con
        // la cita. Lo hacía el portal buscando el encuentro, y esa búsqueda pide leer el expediente:
        // a recepción le fallaba y el encuentro quedaba colgado hasta el barrido del día siguiente.
        if (status is "cancelled" or "noshow")
        {
            var planeados = await hapi.SearchResourcesAsync(
                "Encounter", $"appointment={Uri.EscapeDataString($"Appointment/{appointmentId}")}&status=planned", partition, ct);
            foreach (var enc in planeados)
            {
                if ((string?)enc["id"] is { } encId)
                {
                    await hapi.PatchResourceStatusAsync("Encounter", encId, "cancelled", partition, ct);
                }
            }
        }
        return true;
    }
}

public sealed record RescheduleAppointmentCommand(
    string AppointmentId,
    DateTimeOffset Start,
    int MinutesDuration,
    string PractitionerFhirId,
    string? ConsultationType,
    /// <summary>Motivo y notas: solo se escriben si vienen (quien no lee el expediente no los manda).</summary>
    string? Description,
    string? Note);

/// <summary>
/// Reprograma una cita: fecha, hora, duración, médico y tipo de consulta. Todo lo demás —paciente,
/// sede, motivo, notas— se conserva tal como está guardado, y se escribe con <c>ifMatch</c>: si
/// alguien la cambió mientras tanto, falla en vez de pisar el cambio.
///
/// <para>Pasa por las mismas reglas que una cita escrita por el proxy (sede válida, médico asignado
/// a la sede y agendable), con <see cref="FhirLocationGuard"/>.</para>
/// </summary>
public sealed class RescheduleAppointmentHandler(IHapiFhirClient hapi, AgendaScope scope, FhirLocationGuard locationGuard)
{
    public async Task<bool> HandleAsync(AgendaCaller caller, RescheduleAppointmentCommand cmd, CancellationToken ct = default)
    {
        if (cmd.MinutesDuration is < 5 or > 480)
        {
            throw new ArgumentException("La duración tiene que estar entre 5 y 480 minutos.", nameof(cmd));
        }
        if (string.IsNullOrWhiteSpace(cmd.PractitionerFhirId))
        {
            throw new ArgumentException("Elegí el médico.", nameof(cmd));
        }
        var tipo = string.IsNullOrWhiteSpace(cmd.ConsultationType) ? null : ConsultationTypeCodes.FromCode(cmd.ConsultationType);
        if (!string.IsNullOrWhiteSpace(cmd.ConsultationType) && tipo is null)
        {
            throw new ArgumentException($"Tipo de consulta desconocido: '{cmd.ConsultationType}'.", nameof(cmd));
        }

        var partition = $"org-{caller.OrganizationId}";
        var cita = await hapi.ReadResourceAsync("Appointment", cmd.AppointmentId, partition, ct);
        if (cita is null)
        {
            return false;
        }
        var sedes = await scope.AllowedLocationsAsync(caller, null, ct);
        if (!AgendaScope.Visible(sedes, ListAgendaHandler.LocationRef(cita)))
        {
            throw new AgendaScopeException("La cita es de una sede que no tenés asignada.");
        }
        if ((string?)cita["status"] is { } st && st != "booked")
        {
            throw new InvalidOperationException("Solo se reprograma una cita agendada que todavía no llegó.");
        }

        var version = (string?)cita["meta"]?["versionId"];
        var nueva = (JsonObject)cita.DeepClone();
        nueva["start"] = cmd.Start.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        nueva["end"] = cmd.Start.AddMinutes(cmd.MinutesDuration).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        nueva["minutesDuration"] = cmd.MinutesDuration;
        if (tipo is { } t)
        {
            nueva["appointmentType"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = ConsultationTypeCodes.System,
                    ["code"] = ConsultationTypeCodes.ToCode(t),
                    ["display"] = ConsultationTypeCodes.Display(t),
                }),
                ["text"] = ConsultationTypeCodes.Display(t),
            };
        }
        if (cmd.Description is not null)
        {
            if (string.IsNullOrWhiteSpace(cmd.Description)) { nueva.Remove("description"); }
            else { nueva["description"] = cmd.Description.Trim(); }
        }
        if (cmd.Note is not null)
        {
            if (string.IsNullOrWhiteSpace(cmd.Note)) { nueva.Remove("note"); }
            else
            {
                nueva["note"] = new JsonArray(new JsonObject
                {
                    ["text"] = cmd.Note.Trim(),
                    ["time"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                });
            }
        }

        // El médico se reemplaza; paciente, sede y demás participantes quedan como estaban.
        var participantes = (nueva["participant"] as JsonArray ?? [])
            .Where(p => !(((string?)p?["actor"]?["reference"])?.StartsWith("Practitioner/", StringComparison.Ordinal) ?? false))
            .Select(p => p!.DeepClone())
            .ToList();
        participantes.Insert(Math.Min(1, participantes.Count), new JsonObject
        {
            ["actor"] = new JsonObject { ["reference"] = $"Practitioner/{cmd.PractitionerFhirId.Trim()}" },
            ["status"] = "accepted",
            ["required"] = true,
        });
        nueva["participant"] = new JsonArray([.. participantes]);

        // Mismas reglas que el proxy para una cita: sede válida y médico de la sede, agendable.
        var descriptor = FhirRequestParser.Parse("PUT", $"Appointment/{cmd.AppointmentId}")
            ?? throw new InvalidOperationException("No se pudo describir la escritura de la cita.");
        var decision = await locationGuard.CheckAsync(
            new FhirCallerContext(caller.OrganizationId, caller.UserId, caller.Permissions, caller.PractitionerFhirId),
            descriptor, nueva.ToJsonString(), ct);
        if (decision is { Allowed: false })
        {
            throw new ArgumentException(decision.Reason ?? "La cita no cumple las reglas de la agenda.", nameof(cmd));
        }

        var request = new JsonObject { ["method"] = "PUT", ["url"] = $"Appointment/{cmd.AppointmentId}" };
        if (version is not null)
        {
            request["ifMatch"] = $"W/\"{version}\"";
        }
        await hapi.ExecuteTransactionAsync(new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(new JsonObject { ["resource"] = nueva, ["request"] = request }),
        }, partition, ct);
        return true;
    }
}
