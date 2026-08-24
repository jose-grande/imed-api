using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// Recepción por requisición (rediseño R1/R3): una orden-panel se resuelve por
/// encuentro, se expande a sus pruebas agrupadas por tipo de muestra, y se recibe
/// como una muestra N:M. Más la cola de órdenes pendientes (R1-F5).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabRequisitionTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var organizationId = Guid.NewGuid();
        // Las lecturas del catálogo son estrictas por org (copy-on-create, rev 53): sin
        // aprovisionar, la org no tiene pruebas ni paneles y la orden no resuelve.
        await fixture.ProvisionLabCatalogAsync(organizationId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return fixture.CreateClientWithToken(token);
    }

    [Fact]
    public async Task ResolveYReciben_ordenPanel_expandeYAgrupaPorTipoDeMuestra()
    {
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        // CMP está sembrado. Se ordena como lo escribe el cliente real: coding local del panel,
        // sin LOINC —los paneles de la clínica no tienen uno—. Antes esta prueba sembraba `CMP`
        // bajo el sistema LOINC, que es una forma que ninguna orden real tiene, y por eso no vio
        // que la recepción buscaba el catálogo por el campo equivocado.
        // Sus pruebas son de química → "sangre".
        var srId = fixture.Hapi.SeedServiceRequest(
            "pat-req-1", loinc: null, "Perfil metabolico completo", encounter: enc, requisition: enc,
            localCode: "CMP", localSystem: "http://imed.local/panel");

        // F2 (rev 31): resolver también filtra por órdenes ingresadas.
        var checkIn = await client.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc });
        checkIn.StatusCode.Should().Be(HttpStatusCode.OK);

        // Resolver: la orden-panel se expande y agrupa por tipo de muestra.
        var view = await (await client.GetAsync($"/api/lab/reception/requisition/{enc}"))
            .Content.ReadFromJsonAsync<RequisitionView>();
        view.Should().NotBeNull();
        view!.Groups.Should().NotBeEmpty("la orden-panel debe expandirse a un grupo de tipo de muestra");
        var blood = view.Groups.Should().ContainSingle(g => g.SpecimenTypeCode == "BLOOD").Subject;
        blood.Orders.Should().Contain(o => o.ServiceRequestId == srId);

        // Recibir: una muestra para el grupo (N:M) → accession + Received.
        var receiveBody = new
        {
            requisition = enc,
            groups = new[]
            {
                new
                {
                    specimenTypeCode = blood.SpecimenTypeCode,
                    specimenTypeName = blood.SpecimenTypeName,
                    tubeCode = blood.TubeCode,
                    serviceRequestIds = new[] { srId },
                    collectedDateTime = (string?)null,
                    fastingSatisfied = (bool?)true,
                    reject = false,
                    rejectReason = (string?)null,
                    collectionMethod = (string?)null,
                    collectionBodySite = (string?)null,
                },
            },
        };
        var receiveResp = await client.PostAsJsonAsync("/api/lab/reception/receive-requisition", receiveBody);
        receiveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await receiveResp.Content.ReadFromJsonAsync<ReceiveGroupResult[]>();
        results.Should().ContainSingle();
        results![0].Status.Should().Be("Received");
        results[0].AccessionNumber.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnPanelPropio_entraALaColaDeLaClinica_noSeRefiereAExterno()
    {
        // El síntoma en pantalla era "ningún examen es realizable en esta ubicación" para órdenes
        // de paneles que la clínica sí procesa. La causa: la orden lleva el código del panel en su
        // propio coding local, y la recepción buscaba el catálogo por `LoincCode` —nulo en esa
        // orden—, así que no encontraba nada y refería todo a un laboratorio externo.
        //
        // El costo no es cosmético: el check-in marca `Referred` en el intake, o sea que deja
        // registrado que el paciente se va a hacer los exámenes afuera.
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        var srId = fixture.Hapi.SeedServiceRequest(
            "pat-req-3", loinc: null, "Hemograma completo", encounter: enc, requisition: enc,
            localCode: "CBC", localSystem: "http://imed.local/panel");

        var checkIn = await client.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc });
        checkIn.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await checkIn.Content.ReadFromJsonAsync<CheckInResult>();

        result!.Queued.Should().Contain(e => e.ServiceRequestId == srId,
            "CBC está en el catálogo de la org y sus pruebas están ofertadas");
        result.External.Should().BeEmpty("no hay nada que referir afuera");
    }

    [Fact]
    public async Task Pending_listaOrdenesActivasSinRecibir()
    {
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        var srId = fixture.Hapi.SeedServiceRequest("pat-req-2", "2345-7", "Glucosa", encounter: enc, requisition: enc);

        // F2 (rev 31): la cola solo muestra órdenes **ingresadas**. Sin registrar el ingreso
        // la orden no aparece — es el comportamiento buscado, no una omisión.
        var checkIn = await client.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc });
        checkIn.StatusCode.Should().Be(HttpStatusCode.OK);

        var pending = await (await client.GetAsync("/api/lab/reception/pending"))
            .Content.ReadFromJsonAsync<PendingOrder[]>();
        pending.Should().NotBeNull();
        pending!.Should().Contain(p => p.ServiceRequestId == srId);
    }
}
