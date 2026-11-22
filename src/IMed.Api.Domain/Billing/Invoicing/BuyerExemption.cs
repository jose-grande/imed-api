namespace IMed.Api.Domain.Billing.Invoicing;

/// <summary>
/// Datos del adquirente exonerado (Reglamento del Régimen de Facturación, art. 10.8 y 11.4): el
/// número de la Orden de Compra Exenta, el de la Constancia del Registro de Exonerados o el del
/// Registro de la SAG. Con cualquiera de ellos la venta gravada se factura como exonerada: sin ISV
/// y en el renglón "Importe exonerado".
/// </summary>
public sealed record BuyerExemption(
    string? PurchaseOrderNumber,
    string? ExemptRegistryNumber,
    string? SagRegistryNumber)
{
    public const int MaxLength = 50;

    /// <summary>Recortado, y <c>null</c> si no trae ningún número: así "exonerado" es un solo chequeo.</summary>
    public static BuyerExemption? Normalize(BuyerExemption? e)
    {
        if (e is null)
        {
            return null;
        }
        var oce = Limpio(e.PurchaseOrderNumber);
        var constancia = Limpio(e.ExemptRegistryNumber);
        var sag = Limpio(e.SagRegistryNumber);
        return oce is null && constancia is null && sag is null ? null : new BuyerExemption(oce, constancia, sag);
    }

    private static string? Limpio(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }
        var t = v.Trim();
        if (t.Length > MaxLength)
        {
            throw new ArgumentException($"Los números de exoneración van hasta {MaxLength} caracteres.", nameof(v));
        }
        return t;
    }
}
