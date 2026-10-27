namespace IMed.Api.Domain.Billing.Invoicing;

/// <summary>
/// El artefacto <b>legal</b>: el documento con número fiscal (FSF §5.2).
///
/// <para><b>Inmutable tras emisión.</b> Solo dos cosas cambian después: el estado y los datos de
/// la anulación. Todo lo demás es una fotografía del momento en que se emitió, y tiene que poder
/// reimprimirse idéntico dentro de cinco años — cuando el CAI ya venció, el precio cambió tres
/// veces y el ítem ya no está en el catálogo.</para>
///
/// <para><b>Los cuatro segmentos van en columnas separadas</b> y no en un string, porque hay que
/// poder filtrar por punto de emisión, validar contra el rango autorizado y detectar huecos por
/// segmento. <see cref="FullNumber"/> es la copia denormalizada para buscar: "la factura
/// 001-002-01-00004521" es lo que el paciente trae escrito en el papel.</para>
/// </summary>
public sealed class FiscalDocument
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid LocationId { get; private set; }
    public Guid FiscalAuthorizationId { get; private set; }
    public Guid DocumentTypeId { get; private set; }

    // ── Los cuatro segmentos ──
    public string EstablishmentCode { get; private set; } = string.Empty;
    public string EmissionPointCode { get; private set; } = string.Empty;
    public string DocumentTypeCode { get; private set; } = string.Empty;
    public long Sequence { get; private set; }
    public string FullNumber { get; private set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; private set; }

    // ── Emisor y comprador, congelados ──
    // El emisor se congela entero —no solo el RTN— porque la razón social, su domicilio y la
    // dirección de la sede pueden cambiar después, y una reimpresión tiene que salir idéntica
    // al original (FAC-9).
    public Guid? LegalEntityId { get; private set; }
    public string? IssuerTaxId { get; private set; }
    public string? IssuerLegalName { get; private set; }
    public string? IssuerTradeName { get; private set; }
    public string? IssuerFiscalAddress { get; private set; }
    public string? EstablishmentAddress { get; private set; }
    public Guid? BuyerPartyId { get; private set; }
    public string? BuyerTaxId { get; private set; }
    public string BuyerName { get; private set; } = string.Empty;

    // ── Subtotales por tratamiento tributario ──
    // Separados porque es lo que el contador necesita para declarar: exento, exonerado y cada
    // tasa gravada van en renglones distintos de la declaración.
    public decimal SubtotalExempt { get; private set; }
    public decimal SubtotalExonerated { get; private set; }
    public decimal SubtotalTaxed15 { get; private set; }
    public decimal SubtotalTaxed18 { get; private set; }
    public decimal DiscountTotal { get; private set; }
    public decimal TaxTotal { get; private set; }
    public decimal GrandTotal { get; private set; }

    /// <summary>Importe en letras, como exige el documento impreso.</summary>
    public string? AmountInWords { get; private set; }

    // ── Fotografía de la autorización ──
    // El documento tiene que poder explicarse aunque la autorización ya no exista en la tabla.
    public string CaiSnapshot { get; private set; } = string.Empty;
    public string RangeSnapshot { get; private set; } = string.Empty;
    public DateOnly LimitDateSnapshot { get; private set; }

    public FiscalDocumentStatus Status { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }
    public string? VoidReason { get; private set; }

    /// <summary>Documento que este reemplaza: nota de crédito → factura original.</summary>
    public Guid? ReplacesDocumentId { get; private set; }

    public Guid InvoiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private FiscalDocument() { }

    public static FiscalDocument Issue(
        Guid organizationId,
        Guid locationId,
        Guid fiscalAuthorizationId,
        Guid documentTypeId,
        Guid invoiceId,
        string establishmentCode,
        string emissionPointCode,
        string documentTypeCode,
        long sequence,
        string fullNumber,
        string buyerName,
        string caiSnapshot,
        string rangeSnapshot,
        DateOnly limitDateSnapshot,
        decimal subtotalExempt,
        decimal subtotalExonerated,
        decimal subtotalTaxed15,
        decimal subtotalTaxed18,
        decimal discountTotal,
        decimal taxTotal,
        decimal grandTotal,
        string? amountInWords = null,
        string? issuerTaxId = null,
        Guid? buyerPartyId = null,
        string? buyerTaxId = null,
        Guid? replacesDocumentId = null,
        TimeProvider? timeProvider = null,
        IssuerSnapshot? issuer = null)
    {
        if (string.IsNullOrWhiteSpace(fullNumber))
        {
            throw new ArgumentException("El número fiscal es obligatorio.", nameof(fullNumber));
        }
        if (string.IsNullOrWhiteSpace(buyerName))
        {
            throw new ArgumentException(
                "El comprador es obligatorio: es lo que sale impreso.", nameof(buyerName));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FiscalDocument
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LocationId = locationId,
            FiscalAuthorizationId = fiscalAuthorizationId,
            DocumentTypeId = documentTypeId,
            InvoiceId = invoiceId,
            EstablishmentCode = establishmentCode,
            EmissionPointCode = emissionPointCode,
            DocumentTypeCode = documentTypeCode,
            Sequence = sequence,
            FullNumber = fullNumber,
            IssuedAt = now,
            IssuerTaxId = issuerTaxId,
            LegalEntityId = issuer?.LegalEntityId,
            IssuerLegalName = issuer?.LegalName,
            IssuerTradeName = issuer?.TradeName,
            IssuerFiscalAddress = issuer?.FiscalAddress,
            EstablishmentAddress = issuer?.EstablishmentAddress,
            BuyerPartyId = buyerPartyId,
            BuyerTaxId = buyerTaxId,
            BuyerName = buyerName.Trim(),
            SubtotalExempt = subtotalExempt,
            SubtotalExonerated = subtotalExonerated,
            SubtotalTaxed15 = subtotalTaxed15,
            SubtotalTaxed18 = subtotalTaxed18,
            DiscountTotal = discountTotal,
            TaxTotal = taxTotal,
            GrandTotal = grandTotal,
            AmountInWords = amountInWords,
            CaiSnapshot = caiSnapshot,
            RangeSnapshot = rangeSnapshot,
            LimitDateSnapshot = limitDateSnapshot,
            Status = FiscalDocumentStatus.Issued,
            ReplacesDocumentId = replacesDocumentId,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Anula el documento. <b>El correlativo queda consumido</b> — no vuelve al pozo. Los huecos
    /// que eso deja son legítimos y explicables ante una auditoría; un número reutilizado no.
    /// </summary>
    public void Void(string reason, TimeProvider? timeProvider = null)
    {
        if (Status == FiscalDocumentStatus.Voided)
        {
            return; // idempotente
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Anular exige motivo.", nameof(reason));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        Status = FiscalDocumentStatus.Voided;
        VoidedAt = now;
        VoidReason = reason.Trim();
    }
}

/// <summary>
/// Emisor tal como queda congelado en el documento: la razón social de la sede que emitió, con su
/// domicilio fiscal y la dirección del establecimiento (FAC-9).
/// </summary>
public sealed record IssuerSnapshot(
    Guid LegalEntityId,
    string LegalName,
    string? TradeName,
    string? FiscalAddress,
    string? EstablishmentAddress);
