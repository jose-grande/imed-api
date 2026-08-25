namespace IMed.Api.Domain.Billing.Catalog;

/// <summary>
/// Un precio resuelto, con la razón por la que es ese y no otro.
/// </summary>
/// <param name="Price">Lo que se cobra.</param>
/// <param name="Source">De qué escalón salió.</param>
/// <param name="PriceListId">Tarifario que ganó, o null si fue el precio base del ítem.</param>
/// <param name="PriceListName">Nombre del tarifario, para poder decirlo en pantalla.</param>
/// <param name="MinPrice">Piso de descuento que aplica a este precio.</param>
/// <param name="DiscountPctMax">Tope de descuento propio de la línea.</param>
public sealed record ResolvedPrice(
    decimal Price,
    PriceSource Source,
    Guid? PriceListId,
    string? PriceListName,
    decimal? MinPrice,
    decimal? DiscountPctMax)
{
    /// <summary>
    /// Explicación en una línea, para mostrarla al lado del precio.
    ///
    /// <para>"¿Por qué a este paciente se le cobró esto?" es la pregunta más frecuente en una
    /// auditoría de facturación, y hoy en los sistemas locales se responde llamando a la
    /// administración. Que la pantalla lo diga sola es un diferenciador barato.</para>
    /// </summary>
    public string Explain() => Source switch
    {
        PriceSource.AgreementList => $"Convenio: {PriceListName}",
        PriceSource.PromotionalList => $"Promoción vigente: {PriceListName}",
        PriceSource.EmployeeList => $"Tarifa de personal: {PriceListName}",
        PriceSource.PublicList => $"Lista pública: {PriceListName}",
        _ => "Precio base del catálogo (ningún tarifario aplicaba)",
    };
}

/// <summary>Un candidato para resolver: la lista y su precio para el ítem.</summary>
public sealed record PriceCandidate(PriceList List, PriceListItem Item);

/// <summary>
/// Resuelve qué precio se cobra (FSF bloque 2).
///
/// <para><b>El algoritmo está declarado, no implícito.</b> Convenio del pagador → promoción
/// vigente → tarifa de personal → lista pública → precio base del ítem. La primera que resuelve
/// gana, y la decisión se registra.</para>
///
/// <para><b>Es una función pura sobre datos ya cargados</b> y no una consulta con <c>ORDER BY</c>.
/// Dos razones: la regla de precedencia es política del negocio y tiene que poder leerse sin
/// abrir un SQL, y probar cada escalón contra una base exige sembrar cinco tablas por caso — lo
/// que en la práctica significa que no se prueban todos.</para>
/// </summary>
public static class PriceResolver
{
    /// <summary>Orden de precedencia. El índice más bajo gana.</summary>
    private static int Rank(PriceListKind kind) => kind switch
    {
        PriceListKind.Agreement => 0,
        PriceListKind.Promotional => 1,
        PriceListKind.Employee => 2,
        PriceListKind.Public => 3,
        _ => 99,
    };

    private static PriceSource SourceOf(PriceListKind kind) => kind switch
    {
        PriceListKind.Agreement => PriceSource.AgreementList,
        PriceListKind.Promotional => PriceSource.PromotionalList,
        PriceListKind.Employee => PriceSource.EmployeeList,
        _ => PriceSource.PublicList,
    };

    /// <summary>
    /// Elige el precio del ítem entre los candidatos vigentes el día dado.
    /// </summary>
    /// <param name="item">El cobrable. Su <c>DefaultPrice</c> es el último recurso.</param>
    /// <param name="candidates">
    /// Listas con su precio para este ítem. Se filtran por vigencia acá y no en la consulta para
    /// que la regla de "vigente" viva en un solo lugar.
    /// </param>
    /// <param name="day">Día de la clínica.</param>
    /// <param name="payerContractId">
    /// Convenio del pagador de esta cuenta, si lo hay. Sin él, las listas de convenio no aplican:
    /// aplicar el precio de un convenio a quien no lo tiene es regalar el descuento negociado.
    /// </param>
    public static ResolvedPrice Resolve(
        BillableItem item,
        IEnumerable<PriceCandidate> candidates,
        DateOnly day,
        Guid? payerContractId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(candidates);

        var elegible = candidates
            .Where(c => c.Item.BillableItemId == item.Id)
            .Where(c => c.List.IsInEffectOn(day) && c.Item.IsInEffectOn(day))
            .Where(c => c.List.Kind != PriceListKind.Agreement
                        || (payerContractId is not null && c.List.PayerContractId == payerContractId))
            .OrderBy(c => Rank(c.List.Kind))
            // Dentro del mismo tipo manda la prioridad declarada, y si empatan, la que arrancó
            // después: es la más reciente y la que alguien configuró último.
            .ThenByDescending(c => c.List.Priority)
            .ThenByDescending(c => c.List.ValidFrom)
            .FirstOrDefault();

        if (elegible is null)
        {
            return new ResolvedPrice(
                item.DefaultPrice, PriceSource.ItemDefault, null, null, null, null);
        }

        return new ResolvedPrice(
            elegible.Item.Price,
            SourceOf(elegible.List.Kind),
            elegible.List.Id,
            elegible.List.Name,
            elegible.Item.MinPrice,
            elegible.Item.DiscountPctMax);
    }
}
