using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Cómo se mide el TAT, en un solo lugar para la worklist y el dashboard (LIS-DEC-06): desde que
/// la muestra se recibe en el laboratorio hasta que el informe se libera.
///
/// <para><b>El reloj se detiene al liberar.</b> La worklist medía siempre <c>ahora − recepción</c>,
/// así que una orden validada seguía sumando minutos para siempre y aparecía fuera de objetivo
/// aunque se hubiera liberado a tiempo. El dashboard medía hasta <c>updated_at</c>, que se mueve
/// por cualquier cosa (describir el examen, reclasificar, el backfill de arranque), no por la
/// liberación.</para>
/// </summary>
public static class LabTat
{
    /// <summary>
    /// Cuándo se liberó el informe. <c>validated_at</c> es la marca de liberación; las órdenes
    /// completadas antes de que existiera caen a <c>updated_at</c>, que es lo único que hay.
    /// </summary>
    public static DateTimeOffset? ReleasedAt(OrderIntake intake) =>
        intake.Status == OrderIntakeStatus.Completed ? intake.ValidatedAt ?? intake.UpdatedAt : null;

    /// <summary>
    /// Minutos de TAT: hasta la liberación si ya se liberó (TAT final), hasta <paramref name="now"/>
    /// si sigue en curso. Null si la muestra no se recibió.
    /// </summary>
    public static int? Minutes(OrderIntake intake, DateTimeOffset now)
    {
        if (intake.ReceivedAt is not { } received)
        {
            return null;
        }
        var end = ReleasedAt(intake) ?? now;
        return (int)Math.Max(0, (end - received).TotalMinutes);
    }

    /// <summary>
    /// Objetivo por prioridad. El objetivo fino por prueba (<c>tat_target</c>) sigue pendiente:
    /// su código es el del catálogo y la orden guarda LOINC.
    /// </summary>
    public static int DefaultTargetMinutes(LabRequestPriority priority) => priority switch
    {
        LabRequestPriority.Stat => 30,
        LabRequestPriority.Asap => 45,
        LabRequestPriority.Urgent => 45,
        _ => 60,
    };
}
