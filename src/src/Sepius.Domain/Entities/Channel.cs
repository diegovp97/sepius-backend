namespace Sepius.Domain.Entities;

/// <summary>
/// Entidad de dominio que representa un canal de Twitch monitorizado.
///
/// PATRÓN DDD: Las entidades encapsulan su estado. El constructor privado
/// fuerza el uso del factory method estático <see cref="Create"/>,
/// garantizando que el objeto siempre nace en un estado válido.
/// En JS/TS sería una clase con el constructor privado y un método estático factory.
/// </summary>
public sealed class Channel
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsMonitored { get; private set; }
    public DateTime AddedAt { get; private set; }

    private Channel() { }

    /// <summary>
    /// Crea un canal nuevo en estado activo.
    /// </summary>
    public static Channel Create(string name)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace es una helper de .NET 8
        // que lanza de forma descriptiva. Equivalente a: if (!name?.trim()) throw new Error(...)
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));

        return new Channel
        {
            Id = Guid.NewGuid(),
            Name = name.ToLowerInvariant().Trim(),
            IsMonitored = true,
            AddedAt = DateTime.UtcNow
        };
    }

    public void Pause() => IsMonitored = false;
    public void Resume() => IsMonitored = true;
}
