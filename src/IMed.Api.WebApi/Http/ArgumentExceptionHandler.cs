using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace IMed.Api.WebApi.Http;

/// <summary>
/// Traduce <see cref="ArgumentException"/> a <c>400 Bad Request</c>.
///
/// <para>Un argumento inválido es, por definición, un problema de la solicitud y no del servidor.
/// Sin este mapeo, la validación que los handlers hacen con <c>ArgumentException</c> llega al
/// cliente como un 500 en los endpoints que no la atrapan a mano — y un 500 le dice al cliente
/// "reintentá", que es exactamente lo que no debe hacer con un dato mal formado.</para>
///
/// <para>Los endpoints que ya la atrapan y arman su propio <c>ProblemDetails</c> siguen igual:
/// este handler solo ve lo que nadie manejó.</para>
/// </summary>
public sealed class ArgumentExceptionHandler(ILogger<ArgumentExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ArgumentException argEx)
        {
            return false;
        }

        logger.LogInformation(
            "Solicitud rechazada por argumento inválido en {Path}: {Message}",
            httpContext.Request.Path, argEx.Message);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(
            new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                title = "Solicitud inválida",
                status = StatusCodes.Status400BadRequest,
                detail = argEx.Message,
            },
            cancellationToken);
        return true;
    }
}
