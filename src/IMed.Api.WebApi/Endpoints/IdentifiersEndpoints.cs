using IMed.Api.Application.Identifiers;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints del registry de identifier types (Épica H1):
/// <list type="bullet">
///   <item><c>GET /api/identifiers/types?country=HN</c> — populación del dropdown
///   de tipo en el form de creación de Patient.</item>
///   <item><c>POST /api/identifiers/validate</c> — validación on-blur del campo
///   antes de persistir. Devuelve forma normalizada para auto-formateo en UI.</item>
/// </list>
/// Ambos requieren auth (los consume el frontend logueado).
/// </summary>
public static class IdentifiersEndpoints
{
    public static IEndpointRouteBuilder MapIdentifiersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/identifiers")
            .WithTags("Identifiers")
            .RequireAuthorization();

        group.MapGet("/types", ListTypes)
            .WithName("ListIdentifierTypes")
            .WithSummary("Lista los tipos de identifier disponibles para un país.");

        group.MapPost("/validate", Validate)
            .WithName("ValidateIdentifier")
            .WithSummary("Valida un identifier (type, value) y devuelve la forma canónica si es válido.");

        return app;
    }

    private static IResult ListTypes(
        [FromQuery] string? country,
        ListIdentifierTypesHandler handler)
    {
        var c = country?.Trim() ?? string.Empty;
        if (c.Length == 0)
        {
            return Results.Problem(
                title: "Parámetro requerido",
                detail: "Se requiere el query param 'country' (ej. 'HN').",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var types = handler.Handle(new ListIdentifierTypesQuery(c));
        return Results.Ok(types);
    }

    private static IResult Validate(
        ValidateIdentifierRequest request,
        ValidateIdentifierHandler handler)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Country)
            || string.IsNullOrWhiteSpace(request.Type)
            || request.Value is null)
        {
            return Results.Problem(
                title: "Request inválido",
                detail: "Campos requeridos: country, type, value.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = handler.Handle(new ValidateIdentifierCommand(
            CountryCode: request.Country,
            TypeCode: request.Type,
            Value: request.Value));

        // Tanto válidos como inválidos devuelven 200 — el endpoint es de
        // validación, no de persistencia. El consumer decide cómo mostrar
        // los errores. Sólo 400 cuando el request en sí está malformado.
        return Results.Ok(result);
    }
}

public sealed record ValidateIdentifierRequest(string Country, string Type, string Value);
