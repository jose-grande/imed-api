using FluentAssertions;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Locations;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Cimientos de facturación (FAC-1). Son invariantes de dominio, así que se prueban sin host:
/// lo que importa es que no se pueda construir una fila que después nadie pueda reconciliar.
/// </summary>
public sealed class BillingFoundationsTests
{
    private static readonly Guid Org = Guid.NewGuid();

    [Fact]
    public void UnaParteDeTipoPaciente_exigeElPacienteDelExpediente()
    {
        // Sin el paciente, la parte no se puede vincular con nada del expediente: su estado de
        // cuenta queda flotando y aparece como un segundo "alguien" con el mismo nombre.
        var crear = () => Party.Create(Org, PartyKind.Patient, "Juan Pérez");

        crear.Should().Throw<ArgumentException>()
            .WithMessage("*paciente del expediente*");
    }

    [Fact]
    public void UnClienteDeMostrador_noPuedeApuntarAUnPaciente()
    {
        // La otra mitad de la invariante. Si un `walk_in_customer` pudiera apuntar a un paciente,
        // el índice único de (org, paciente) dejaría de garantizar una parte por paciente.
        var crear = () => Party.Create(
            Org, PartyKind.WalkInCustomer, "Farmacia mostrador", patientFhirId: "pat-1");

        crear.Should().Throw<ArgumentException>()
            .WithMessage("*no puede apuntar a un paciente*");
    }

    [Fact]
    public void UnaVentaDeMostrador_seRegistraSinRtn()
    {
        // Exigir RTN para vender un ibuprofeno convierte cada venta chica en un trámite. El umbral
        // legal se valida al emitir el documento (FAC-5), que es donde la regla aplica.
        var parte = Party.Create(Org, PartyKind.WalkInCustomer, "Cliente de mostrador");

        parte.TaxId.Should().BeNull();
        parte.Active.Should().BeTrue();
    }

    [Fact]
    public void CambiarLosDatosDeFacturacion_noCambiaAQuienRepresentaLaParte()
    {
        // `UpdateDetails` no expone Kind ni PatientFhirId a propósito: reapuntar una parte que ya
        // tiene documentos emitidos reescribiría a nombre de quién se emitieron.
        var parte = Party.Create(Org, PartyKind.Patient, "Juan Pérez", patientFhirId: "pat-7");

        parte.UpdateDetails("Juan Pérez Gómez", taxId: "0801199012345", taxIdKind: "RTN");

        parte.LegalName.Should().Be("Juan Pérez Gómez");
        parte.TaxId.Should().Be("0801199012345");
        parte.Kind.Should().Be(PartyKind.Patient);
        parte.PatientFhirId.Should().Be("pat-7", "la parte sigue siendo del mismo paciente");
    }

    [Fact]
    public void UnTopeDeDescuentoPorcentual_noPuedePasarDe100()
    {
        var crear = () => UserAuthorizationLimit.Create(
            Org, Guid.NewGuid(), AuthorizationLimitKind.DiscountPercent, 120m,
            new DateOnly(2026, 8, 1));

        crear.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ElTopeVigente_seResuelvePorFecha()
    {
        // Una auditoría no pregunta cuál es el tope: pregunta cuál regía el día de la venta.
        var tope = UserAuthorizationLimit.Create(
            Org, Guid.NewGuid(), AuthorizationLimitKind.DiscountAmount, 500m,
            validFrom: new DateOnly(2026, 8, 1), validTo: new DateOnly(2026, 8, 31));

        tope.IsInEffectOn(new DateOnly(2026, 7, 31)).Should().BeFalse();
        tope.IsInEffectOn(new DateOnly(2026, 8, 1)).Should().BeTrue("la vigencia incluye el primer día");
        tope.IsInEffectOn(new DateOnly(2026, 8, 31)).Should().BeTrue("y el último");
        tope.IsInEffectOn(new DateOnly(2026, 9, 1)).Should().BeFalse();
    }

    [Fact]
    public void SuperarElTope_abrePorDefectoElFlujoDeAutorizacion()
    {
        // Lo que se rechaza sin salida se evade por fuera del sistema: el cajero llama al
        // supervisor para que le preste la contraseña y el control desaparece sin dejar rastro.
        var tope = UserAuthorizationLimit.Create(
            Org, Guid.NewGuid(), AuthorizationLimitKind.DiscountAmount, 500m, new DateOnly(2026, 8, 1));

        tope.RequiresApprovalAbove.Should().BeTrue();
    }

    [Fact]
    public void ElCodigoDeEstablecimiento_sonExactamenteTresDigitos()
    {
        // Se valida en el dominio y no en la pantalla porque un código mal formado no se descubre
        // al guardarlo: se descubre cuando el primer documento sale con un número que la
        // autoridad fiscal no reconoce.
        var sede = OrganizationLocation.Create(Org, "CENTRAL", "Sede central", Guid.NewGuid());

        var dosDigitos = () => sede.SetFiscalEstablishmentCode("01");
        var conLetras = () => sede.SetFiscalEstablishmentCode("00A");

        dosDigitos.Should().Throw<ArgumentException>();
        conLetras.Should().Throw<ArgumentException>();

        sede.SetFiscalEstablishmentCode("001");
        sede.FiscalEstablishmentCode.Should().Be("001");

        // Vaciarlo es válido: una sede puede dejar de facturar sin dejar de existir.
        sede.SetFiscalEstablishmentCode(null);
        sede.FiscalEstablishmentCode.Should().BeNull();
    }
}
