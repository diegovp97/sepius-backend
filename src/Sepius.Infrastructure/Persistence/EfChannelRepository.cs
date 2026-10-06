using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.Persistence;

/// <summary>
/// Canales en Postgres. Se necesita ahora que hay dos procesos (API y grabador) que deben ver
/// la misma lista; con el repositorio en memoria cada uno tendría la suya.
/// Es singleton y abre un ámbito por operación porque AppDbContext es scoped.
/// </summary>
public sealed class EfChannelRepository(IServiceScopeFactory scopes) : IChannelRepository
{
    public async Task<IEnumerable<Channel>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Channels.AsNoTracking().OrderBy(c => c.AddedAt).ToListAsync(ct);
    }

    public async Task<Channel?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = name.Trim().ToLowerInvariant();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Name == normalized, ct);
    }

    public async Task AddAsync(Channel channel, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Channels.Add(channel);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Otro proceso (API o grabador) puede haberlo sembrado a la vez: entonces no es un error.
            using var check = scopes.CreateScope();
            var db2 = check.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db2.Channels.AnyAsync(c => c.Name == channel.Name, ct))
                throw;
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return;
        db.Channels.Remove(channel);
        await db.SaveChangesAsync(ct);
    }
}
