using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Seed mock de reglas clínicas globales (Épica LIS-5). Idempotente por
/// presencia de reglas globales. Datos de demostración.
/// </summary>
public sealed class LabRuleSeeder(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<LabRuleSeeder> logger) : IHostedService
{
    private static readonly (string Code, string Name, RuleAction Action, RuleSeverity Severity, string Message, string Logic)[] Rules =
    [
        ("DELTA_CHECK", "Delta check (cambio >20%)", RuleAction.RequireReview, RuleSeverity.Warning,
            "Cambio mayor al 20% respecto al resultado previo — requiere revisión.",
            "{\">\":[{\"var\":\"deltaPct\"},20]}"),
        ("CRITICAL_FLAG", "Valor crítico (HH/LL)", RuleAction.FlagCritical, RuleSeverity.Critical,
            "Valor crítico — comunicar de inmediato al médico.",
            "{\"in\":[{\"var\":\"flag\"},[\"HH\",\"LL\"]]}"),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ILabRuleRepository>();

            bool hasGlobals;
            try
            {
                hasGlobals = await repo.AnyGlobalRulesAsync(cancellationToken);
            }
            catch (Exception)
            {
                // El host puede arrancar antes de aplicar migraciones (tests) — migrar y reintentar.
                logger.LogWarning("Tabla lab_rule aún no existe; ejecutando migrate y reintentando.");
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                hasGlobals = await repo.AnyGlobalRulesAsync(cancellationToken);
            }

            if (hasGlobals)
            {
                logger.LogDebug("Reglas globales ya sembradas; skip.");
                return;
            }

            var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            foreach (var r in Rules)
            {
                var rule = LabRule.Create(r.Code, r.Name, RuleScope.Global, r.Action,
                    severity: r.Severity, message: r.Message, timeProvider: timeProvider);
                var version = LabRuleVersion.Create(rule.Id, 1, r.Logic, today, RuleVersionStatus.Active, timeProvider);
                await repo.AddRuleWithVersionAsync(rule, version, cancellationToken);
            }
            logger.LogInformation("Lab rules seed cargado: {Count} reglas globales.", Rules.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LabRuleSeeder falló; el motor de reglas puede estar sin reglas.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
