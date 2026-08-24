namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>Acción que dispara una regla del laboratorio (Épica LIS-5).</summary>
public enum RuleAction
{
    /// <summary>Marca el resultado como valor crítico (bloquea auto-validación, escala en LIS-7).</summary>
    FlagCritical = 0,
    /// <summary>Requiere revisión manual (bloquea auto-validación) — ej. delta check.</summary>
    RequireReview = 1,
    /// <summary>Permite auto-validar (regla de confianza).</summary>
    AutoValidate = 2,
    /// <summary>Solo alerta (no bloquea).</summary>
    Alert = 3,
}

/// <summary>Alcance de una regla: global o por prueba (LOINC).</summary>
public enum RuleScope
{
    Global = 0,
    Test = 1,
}

/// <summary>Severidad de una regla / su alerta.</summary>
public enum RuleSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>Ciclo de vida de una versión de regla.</summary>
public enum RuleVersionStatus
{
    Draft = 0,
    Active = 1,
    Retired = 2,
}
