using Microsoft.EntityFrameworkCore;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<UploadPipeline> UploadPipelines => Set<UploadPipeline>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Clip> Clips => Set<Clip>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Clip>(e =>
        {
            e.ToTable("clips");
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasMaxLength(32).IsRequired();
            e.Property(x => x.Platform).HasMaxLength(16).IsRequired();
            e.Property(x => x.Title).HasMaxLength(80).IsRequired();
            e.Property(x => x.Nickname).HasMaxLength(32).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(64).IsRequired();
            e.Property(x => x.ThumbFileName).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.ToTable("channels");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<UploadPipeline>(e =>
        {
            e.ToTable("upload_pipeline");
            e.HasKey(x => x.FilePath);
            e.Property(x => x.DriveStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.YouTubeStatus).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.UpdatedAt);
        });
    }
}
