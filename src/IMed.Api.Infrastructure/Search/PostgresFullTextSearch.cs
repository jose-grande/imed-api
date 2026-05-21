using System.Globalization;
using System.Text;

namespace IMed.Api.Infrastructure.Search;

/// <summary>
/// Helpers para construir queries Postgres FTS (tsquery) desde input de usuario.
///
/// Cada catálogo (icd11_catalog, loinc_catalog, ...) declara una columna <c>tsv</c>
/// GENERATED ALWAYS AS <c>to_tsvector('spanish', unaccent(coalesce(display_es,'') || ' ' || coalesce(display_en,'')))</c>
/// STORED con índice GIN. Las queries se construyen con <see cref="BuildTsQuery"/>
/// (token + prefix-match en el último) y se aplican vía Npgsql FTS extensions:
/// <code>
/// var tsQuery = PostgresFullTextSearch.BuildTsQuery(term);
/// return await db.Icd11Catalog
///     .Where(x => x.Active &amp;&amp; x.SearchVector.Matches(EF.Functions.ToTsQuery("spanish", tsQuery)))
///     .OrderByDescending(x => x.SearchVector.RankCoverDensity(EF.Functions.ToTsQuery("spanish", tsQuery)))
///     .Take(limit)
///     .ToListAsync(ct);
/// </code>
/// El input se desacentúa en C# (<see cref="Unaccent"/>) antes de pasarse a
/// <c>to_tsquery</c>, simétrico con el <c>unaccent()</c> aplicado en la columna.
/// Esto evita depender del extension <c>unaccent</c> en runtime para cada query
/// (sólo se usa al momento de generar el tsv en el INSERT).
/// </summary>
public static class PostgresFullTextSearch
{
    /// <summary>
    /// Construye una tsquery con prefix-match en el último token. Devuelve
    /// string vacío si el input no produce tokens útiles — el caller debe
    /// short-circuit a array vacío en ese caso.
    /// </summary>
    public static string BuildTsQuery(string term)
    {
        var tokens = Tokenize(term);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" & ");
            }
            sb.Append(tokens[i]);
            if (i == tokens.Count - 1)
            {
                sb.Append(":*");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Como <see cref="BuildTsQuery"/> pero sin prefix-match en el último token —
    /// matchea palabras enteras. Útil para búsquedas estrictas.
    /// </summary>
    public static string BuildPlainQuery(string term)
    {
        var tokens = Tokenize(term);
        return tokens.Count == 0 ? string.Empty : string.Join(" & ", tokens);
    }

    /// <summary>
    /// Quita marcas diacríticas. "Cólera" → "Colera", "diábetes" → "diabetes".
    /// Equivalente al extension <c>unaccent</c> de Postgres aplicado en C#.
    /// </summary>
    public static string Unaccent(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static List<string> Tokenize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new List<string>();
        }

        var unaccented = Unaccent(input).ToLowerInvariant();

        // Sustituir cualquier no-alphanumeric por espacio. Esto descarta los
        // operadores de tsquery (&|!()*<>:) y caracteres raros sin necesidad
        // de escaparlos individualmente.
        var sb = new StringBuilder(unaccented.Length);
        foreach (var c in unaccented)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .ToList();
    }
}
