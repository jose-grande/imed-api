using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;

namespace IMed.Api.Application.Billing;

public sealed record FiscalDocumentTypeDto(
    Guid Id, string Code, string Name, string DocumentCode, bool AffectsReceivable, short Sign, bool Active);

public sealed record TaxCodeDto(
    Guid Id, string Code, string Name, decimal Rate, string Treatment, bool Active);

/// <summary>Qué quedó configurado tras aprovisionar el régimen.</summary>
public sealed record FiscalSetupDto(
    string? RegimeCode,
    string? IssuerTaxId,
    string? IssuerLegalName,
    IReadOnlyList<FiscalDocumentTypeDto> DocumentTypes,
    IReadOnlyList<TaxCodeDto> TaxCodes);

public sealed class GetFiscalSetupHandler(IFiscalRepository repo, IClinicClock clock)
{
    public async Task<FiscalSetupDto> HandleAsync(Guid orgId, CancellationToken ct = default)
    {
        var regime = await repo.GetActiveRegimeAsync(orgId, clock.Today(), ct);
        var tipos = await repo.ListDocumentTypesAsync(orgId, ct);
        var tributos = await repo.ListTaxCodesAsync(orgId, ct);

        return new FiscalSetupDto(
            regime?.RegimeCode,
            regime?.IssuerTaxId,
            regime?.IssuerLegalName,
            tipos.Select(t => new FiscalDocumentTypeDto(
                t.Id, t.Code, t.Name, t.DocumentCode, t.AffectsReceivable, t.Sign, t.Active)).ToList(),
            tributos.Select(t => new TaxCodeDto(
                t.Id, t.Code, t.Name, t.Rate, t.Treatment.ToString(), t.Active)).ToList());
    }
}

/// <summary>
/// Deja la organización lista para emitir bajo un régimen: crea el régimen, sus tipos de documento
/// y sus tributos.
///
/// <para><b>Es explícito y no automático al crear la organización.</b> Una clínica que todavía no
/// factura no necesita un régimen fiscal, y sembrárselo sin que lo pida le pone en pantalla una
/// configuración que no entiende y que parece obligatoria. Acá lo dispara quien va a facturar.</para>
///
/// <para><b>Idempotente.</b> Se puede llamar dos veces sin duplicar nada: lo que falta se crea, lo
/// que está se deja. Un botón de configuración que rompe al segundo clic es un botón que la gente
/// no vuelve a tocar.</para>
/// </summary>
public sealed class ProvisionFiscalRegimeHandler(IFiscalRepository repo, IClinicClock clock)
{
    /// <summary>Tipos de documento del SAR: (código, nombre, dígitos, mueve saldo, signo).</summary>
    private static readonly (string Code, string Name, string DocumentCode, bool Receivable, short Sign)[] HnDocumentTypes =
    [
        ("factura", "Factura", "01", true, 1),
        ("nota_debito", "Nota de débito", "02", true, 1),
        ("nota_credito", "Nota de crédito", "03", true, -1),
        ("proforma", "Proforma", "04", false, 0),
    ];

    /// <summary>
    /// Tributos de Honduras: (código, nombre, tasa, tratamiento).
    ///
    /// <para>El grueso de lo que factura una clínica hondureña es <c>EXENTO</c> —servicios médicos,
    /// laboratorio, medicamentos—, pero no todo: la estética paga 15% y la farmacia vende
    /// cosméticos y suplementos que también. Sembrar los tres desde el día uno evita que alguien
    /// resuelva el primer ítem gravado poniéndole "exento" porque es la única opción que ve.</para>
    /// </summary>
    private static readonly (string Code, string Name, decimal Rate, TaxTreatment Treatment)[] HnTaxCodes =
    [
        ("EXENTO", "Exento de ISV", 0m, TaxTreatment.Exempt),
        ("ISV15", "ISV 15%", 15m, TaxTreatment.Taxed),
        ("ISV18", "ISV 18%", 18m, TaxTreatment.Taxed),
        ("EXONERADO", "Exonerado (con constancia)", 0m, TaxTreatment.Exonerated),
    ];

    public async Task<FiscalSetupDto> HandleAsync(
        Guid orgId,
        string regimeCode,
        string? issuerTaxId = null,
        string? issuerLegalName = null,
        CancellationToken ct = default)
    {
        if (!string.Equals(regimeCode, "HN_SAR_CAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"No hay configuración por defecto para el régimen '{regimeCode}'.", nameof(regimeCode));
        }

        var today = clock.Today();
        var regimen = await repo.GetActiveRegimeAsync(orgId, today, ct);

        if (regimen is null)
        {
            await repo.AddRegimeAsync(
                FiscalRegime.Create(
                    orgId, "HN", "HN_SAR_CAI", today,
                    issuerTaxId: issuerTaxId, issuerLegalName: issuerLegalName), ct);
        }
        else if (!string.IsNullOrWhiteSpace(issuerTaxId) || !string.IsNullOrWhiteSpace(issuerLegalName))
        {
            // Volver a llamar con los datos del emisor los completa: el régimen suele configurarse
            // antes de tener el RTN a mano.
            regimen.SetIssuer(
                issuerTaxId ?? regimen.IssuerTaxId,
                issuerLegalName ?? regimen.IssuerLegalName);
            await repo.UpdateRegimeAsync(regimen, ct);
        }

        var tiposExistentes = (await repo.ListDocumentTypesAsync(orgId, ct))
            .Select(t => t.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, name, doc, receivable, sign) in HnDocumentTypes)
        {
            if (tiposExistentes.Contains(code))
            {
                continue;
            }
            await repo.AddDocumentTypeAsync(
                FiscalDocumentType.Create(orgId, code, name, doc, receivable, sign), ct);
        }

        var tributosExistentes = (await repo.ListTaxCodesAsync(orgId, ct))
            .Select(t => t.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, name, rate, treatment) in HnTaxCodes)
        {
            if (tributosExistentes.Contains(code))
            {
                continue;
            }
            await repo.AddTaxCodeAsync(TaxCode.Create(orgId, code, name, rate, treatment), ct);
        }

        return await new GetFiscalSetupHandler(repo, clock).HandleAsync(orgId, ct);
    }
}
