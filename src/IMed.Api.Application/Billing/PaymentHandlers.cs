using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Cash;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Billing;

public sealed record PaymentAllocationDto(Guid InvoiceId, decimal Amount);

public sealed record PaymentDto(
    Guid Id,
    Guid PartyId,
    string Method,
    decimal Amount,
    string? Reference,
    Guid CashSessionId,
    DateTimeOffset ReceivedAt,
    string Status,
    decimal AllocatedAmount,
    decimal ChangeDue,
    IReadOnlyList<PaymentAllocationDto> Allocations);

/// <summary>Una porción del cobro: qué método y cuánto. Es lo que permite el pago mixto.</summary>
public sealed record TenderLine(string Method, decimal Amount, string? Reference);

public sealed record ReceivePaymentCommand(
    Guid OrganizationId,
    Guid SessionId,
    Guid PartyId,
    Guid ReceivedBy,
    IReadOnlyList<Guid> InvoiceIds,
    IReadOnlyList<TenderLine> Tenders);

public sealed record ReceivePaymentResult(
    IReadOnlyList<PaymentDto> Payments,
    decimal Applied,
    decimal ChangeDue,
    string? Code,
    string? Message)
{
    public bool Succeeded => Code is null;

    public static ReceivePaymentResult Reject(string code, string message) =>
        new([], 0m, 0m, code, message);
}

/// <summary>
/// Cobra una o varias facturas con uno o varios métodos (FAC-6).
///
/// <para><b>Multi-método es requisito, no lujo.</b> "Parte en efectivo, parte con tarjeta" es lo
/// normal cuando el paciente no llega al total con lo que trae. Cada porción es un
/// <see cref="Payment"/> propio —tienen referencias distintas y concilian por separado— y todas
/// se aplican al mismo conjunto de facturas.</para>
///
/// <para><b>El vuelto sale solo del efectivo.</b> De una tarjeta no se devuelve dinero: si el
/// total tendido supera lo aplicado y no hubo efectivo, es un error de captura y se rechaza en
/// vez de generar un vuelto que la caja no puede dar.</para>
/// </summary>
public sealed class ReceivePaymentHandler(
    ICashRepository cash, IInvoiceRepository invoices)
{
    public async Task<ReceivePaymentResult> HandleAsync(
        ReceivePaymentCommand cmd, CancellationToken ct = default)
    {
        var turno = await cash.GetSessionAsync(cmd.OrganizationId, cmd.SessionId, ct);
        if (turno is null)
        {
            return ReceivePaymentResult.Reject("turno_inexistente", "El turno no existe.");
        }
        if (!turno.IsOpen)
        {
            return ReceivePaymentResult.Reject(
                "turno_cerrado", "El turno está cerrado: abrí uno nuevo para cobrar.");
        }
        if (cmd.Tenders.Count == 0)
        {
            return ReceivePaymentResult.Reject("sin_pago", "No se recibió ningún pago.");
        }

        // ── Facturas a cobrar, leídas para modificar el saldo ──
        var facturas = new List<Invoice>();
        foreach (var id in cmd.InvoiceIds.Distinct())
        {
            var (f, _) = await invoices.GetForUpdateAsync(cmd.OrganizationId, id, ct);
            if (f is null)
            {
                return ReceivePaymentResult.Reject("factura_inexistente", $"La factura {id} no existe.");
            }
            if (f.Status is InvoiceStatus.Voided or InvoiceStatus.Draft)
            {
                return ReceivePaymentResult.Reject(
                    "factura_no_cobrable", $"Una factura en estado '{f.Status}' no se cobra.");
            }
            if (f.Balance <= 0)
            {
                return ReceivePaymentResult.Reject(
                    "factura_saldada", "Esa factura ya está pagada.");
            }
            facturas.Add(f);
        }

        if (facturas.Count == 0)
        {
            return ReceivePaymentResult.Reject("sin_facturas", "No se indicó qué cobrar.");
        }

        var porCobrar = facturas.Sum(f => f.Balance);
        var tendido = cmd.Tenders.Sum(t => t.Amount);

        if (tendido <= 0)
        {
            return ReceivePaymentResult.Reject("sin_pago", "El monto recibido tiene que ser positivo.");
        }

        var efectivoTendido = cmd.Tenders
            .Where(t => ParseMethod(t.Method) == PaymentMethod.Cash)
            .Sum(t => t.Amount);

        var vuelto = Math.Round(Math.Max(0, tendido - porCobrar), 2, MidpointRounding.AwayFromZero);

        // De una tarjeta no se devuelve efectivo. Si sobra y no hubo efectivo, alguien tecleó mal
        // el monto — y confirmarlo dejaría a la caja debiendo un vuelto que no puede dar.
        if (vuelto > efectivoTendido)
        {
            return ReceivePaymentResult.Reject(
                "vuelto_sin_efectivo",
                $"Sobran L {vuelto:N2} y no hay efectivo suficiente para devolverlos. "
                + "Revisá los montos: de una tarjeta no se da vuelto.");
        }

        // ── Repartir lo tendido entre las facturas, en orden ──
        // El efectivo se aplica último para que el vuelto salga de él: si se aplicara primero,
        // el sobrante quedaría en la tarjeta y no se podría devolver.
        var ordenados = cmd.Tenders
            .OrderBy(t => ParseMethod(t.Method) == PaymentMethod.Cash ? 1 : 0)
            .ToList();

        var pendientes = facturas.ToDictionary(f => f.Id, f => f.Balance);
        var resultado = new List<PaymentDto>();
        var aplicadoTotal = 0m;
        var vueltoTotal = 0m;

        foreach (var tender in ordenados)
        {
            var metodo = ParseMethod(tender.Method);
            Payment pago;
            try
            {
                pago = Payment.Receive(
                    cmd.OrganizationId, cmd.PartyId, cmd.SessionId, metodo,
                    tender.Amount, cmd.ReceivedBy, tender.Reference);
            }
            catch (ArgumentException ex)
            {
                return ReceivePaymentResult.Reject("solicitud_invalida", ex.Message);
            }

            var disponible = tender.Amount;
            foreach (var f in facturas)
            {
                if (disponible <= 0)
                {
                    break;
                }
                var falta = pendientes[f.Id];
                if (falta <= 0)
                {
                    continue;
                }

                var aplicar = Math.Min(disponible, falta);
                pago.Allocate(PaymentAllocation.Create(
                    cmd.OrganizationId, pago.Id, f.Id, aplicar));
                f.ApplyPayment(aplicar);

                pendientes[f.Id] = falta - aplicar;
                disponible -= aplicar;
                aplicadoTotal += aplicar;
            }

            vueltoTotal += pago.ChangeDue;
            await cash.SavePaymentAsync(pago, facturas, ct);
            resultado.Add(ToDto(pago));
        }

        return new ReceivePaymentResult(
            resultado,
            Math.Round(aplicadoTotal, 2, MidpointRounding.AwayFromZero),
            Math.Round(vueltoTotal, 2, MidpointRounding.AwayFromZero),
            null, null);
    }

    private static PaymentMethod ParseMethod(string? method) =>
        Enum.TryParse<PaymentMethod>(method, ignoreCase: true, out var m) ? m : PaymentMethod.Cash;

    internal static PaymentDto ToDto(Payment p) =>
        new(p.Id, p.PartyId, p.Method.ToString(), p.Amount, p.Reference, p.CashSessionId,
            p.ReceivedAt, p.Status.ToString(), p.AllocatedAmount, p.ChangeDue,
            p.Allocations.Select(a => new PaymentAllocationDto(a.InvoiceId, a.Amount)).ToList());
}

public sealed class ListSessionPaymentsHandler(ICashRepository cash)
{
    public async Task<IReadOnlyList<PaymentDto>> HandleAsync(
        Guid orgId, Guid sessionId, CancellationToken ct = default)
    {
        var pagos = await cash.ListPaymentsAsync(orgId, sessionId, ct);
        return pagos.Select(ReceivePaymentHandler.ToDto).ToList();
    }
}
