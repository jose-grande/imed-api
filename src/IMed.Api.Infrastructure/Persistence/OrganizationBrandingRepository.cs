using IMed.Api.Application.Organizations;
using IMed.Api.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class OrganizationBrandingRepository(AppDbContext dbContext, TimeProvider timeProvider)
    : IOrganizationBrandingRepository
{
    public async Task<OrganizationLogoContent?> GetLogoAsync(
        Guid organizationId, CancellationToken ct = default)
    {
        // La proyección trae los bytes solo de esta consulta —la que emite un documento—, y
        // nunca desde la lectura del perfil.
        var row = await dbContext.OrganizationBranding
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.LogoBytes != null)
            .Select(x => new { x.LogoBytes, x.LogoContentType, x.UpdatedAt })
            .FirstOrDefaultAsync(ct);

        if (row?.LogoBytes is null || string.IsNullOrWhiteSpace(row.LogoContentType))
        {
            return null;
        }

        return new OrganizationLogoContent(row.LogoBytes, row.LogoContentType, row.UpdatedAt);
    }

    public async Task<OrganizationLogoInfo?> GetLogoInfoAsync(
        Guid organizationId, CancellationToken ct = default)
    {
        // `length(logo_bytes)` se calcula en PostgreSQL: la pantalla de configuración quiere
        // saber cuánto pesa el logo, no traerlo.
        var row = await dbContext.OrganizationBranding
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.LogoBytes != null)
            .Select(x => new
            {
                x.LogoContentType,
                x.LogoFileName,
                Size = x.LogoBytes!.Length,
                x.UpdatedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null || string.IsNullOrWhiteSpace(row.LogoContentType))
        {
            return null;
        }

        return new OrganizationLogoInfo(
            row.LogoContentType, row.LogoFileName, row.Size, row.UpdatedAt);
    }

    public async Task SaveLogoAsync(
        Guid organizationId,
        byte[] bytes,
        string contentType,
        string? fileName,
        Guid? updatedBy,
        CancellationToken ct = default)
    {
        var branding = await dbContext.OrganizationBranding
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId, ct);

        if (branding is null)
        {
            branding = OrganizationBranding.Create(organizationId, timeProvider);
            dbContext.OrganizationBranding.Add(branding);
        }

        branding.SetLogo(bytes, contentType, fileName, updatedBy, timeProvider);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveLogoAsync(
        Guid organizationId, Guid? updatedBy, CancellationToken ct = default)
    {
        var branding = await dbContext.OrganizationBranding
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId, ct);

        if (branding is null || !branding.HasLogo)
        {
            return false;
        }

        // Se deja la fila y se vacían las columnas: conserva CreatedAt y UpdatedBy, que son el
        // rastro de quién tocó la identidad visual de la organización.
        branding.RemoveLogo(updatedBy, timeProvider);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }
}
