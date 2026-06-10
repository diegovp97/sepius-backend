using System.Collections.Concurrent;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.Persistence;

/// <summary>
/// Implementación en memoria del repositorio de canales.
///
/// PARALELO NODE.JS: Equivale a un simple Map() global en tu módulo de Express.
/// Para persistencia real, reemplaza esta clase por una implementación con
/// EF Core (ORM de .NET) apuntando a SQLite o PostgreSQL. Gracias a la
/// interfaz IChannelRepository, el resto del código NO cambia.
///
/// REGISTRO: Se registra como Singleton en el contenedor DI porque el
/// diccionario en memoria debe ser el mismo durante toda la vida de la app.
/// </summary>
public sealed class InMemoryChannelRepository : IChannelRepository
{
    // ConcurrentDictionary = versión thread-safe de Map en JavaScript.
    // Esencial porque BackgroundService escribe desde otro hilo mientras
    // los controllers leen desde peticiones HTTP simultáneas.
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();

    public Task<IEnumerable<Channel>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IEnumerable<Channel>>(_channels.Values.ToList());

    public Task<Channel?> GetByNameAsync(string name, CancellationToken ct = default)
        => Task.FromResult(
            _channels.Values.FirstOrDefault(c =>
                c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(Channel channel, CancellationToken ct = default)
    {
        _channels[channel.Id] = channel;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        _channels.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
