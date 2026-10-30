using DocumentViewer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocumentViewer.Infrastructure.Persistence;

public sealed class DocumentViewerDbContext(DbContextOptions<DocumentViewerDbContext> options)
    : DbContext(options)
{
    public DbSet<SearchAuditEntity> SearchAudits => Set<SearchAuditEntity>();

    public DbSet<DocumentCacheEntity> DocumentCaches => Set<DocumentCacheEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SearchAuditEntity>(entity =>
        {
            entity.ToTable("search_audit");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .UseIdentityByDefaultColumn();

            entity.Property(e => e.Vin)
                .HasColumnName("vin")
                .HasMaxLength(17)
                .IsRequired();

            entity.Property(e => e.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(e => e.SearchedAt)
                .HasColumnName("searched_at")
                .HasColumnType("timestamptz")
                .IsRequired();

            entity.Property(e => e.DurationMs)
                .HasColumnName("duration_ms")
                .IsRequired();

            entity.Property(e => e.Partial)
                .HasColumnName("partial")
                .IsRequired();

            entity.Property(e => e.SourceReport)
                .HasColumnName("source_report")
                .HasColumnType("jsonb")
                .IsRequired();

            entity.HasIndex(e => new { e.Vin, e.SearchedAt })
                .IsDescending(false, true)
                .HasDatabaseName("ix_search_audit_vin_searched_at");
        });

        modelBuilder.Entity<DocumentCacheEntity>(entity =>
        {
            entity.ToTable("document_metadata_cache");

            entity.HasKey(e => new { e.Vin, e.SourceSystem });

            entity.Property(e => e.Vin)
                .HasColumnName("vin")
                .HasMaxLength(17)
                .IsRequired();

            entity.Property(e => e.SourceSystem)
                .HasColumnName("source_system")
                .HasColumnType("smallint")
                .IsRequired();

            entity.Property(e => e.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(e => e.DocumentCount)
                .HasColumnName("document_count")
                .IsRequired();

            entity.Property(e => e.FetchedAt)
                .HasColumnName("fetched_at")
                .HasColumnType("timestamptz")
                .IsRequired();

            entity.Property(e => e.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamptz")
                .IsRequired();

            entity.HasIndex(e => e.ExpiresAt)
                .HasDatabaseName("ix_document_metadata_cache_expires_at");
        });
    }
}
