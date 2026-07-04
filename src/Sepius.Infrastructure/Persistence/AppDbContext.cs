using Microsoft.EntityFrameworkCore;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AuthUser> AuthUsers => Set<AuthUser>();
}
