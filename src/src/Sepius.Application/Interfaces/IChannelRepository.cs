using Sepius.Domain.Entities;

namespace Sepius.Application.Interfaces;

/// <summary>
/// Contrato para el almacenamiento de canales.
/// Al ser una interfaz en Application, cualquier capa (Infrastructure, Tests)
/// puede proveer su propia implementación. Esto es la "D" de SOLID (Dependency Inversion).
///
/// PARALELO NODE.JS: Equivale a un "service interface" o "repository interface"
/// en arquitecturas limpias de Express/NestJS.
/// </summary>
public interface IChannelRepository
{
    Task<IEnumerable<Channel>> GetAllAsync(CancellationToken ct = default);
    Task<Channel?> GetByNameAsync(string name, CancellationToken ct = default);
    Task AddAsync(Channel channel, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}
