using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocumentViewer.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DocumentViewerDbContext>
{
    public DocumentViewerDbContext CreateDbContext(string[] args)
    {
        var connectionString = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ConnectionStrings__Default"),
            Environment.GetEnvironmentVariable("ConnectionStrings__Migrations"),
            "Host=localhost;Port=5432;Database=document_viewer;Username=postgres;Password=postgres");

        var optionsBuilder = new DbContextOptionsBuilder<DocumentViewerDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        return new DocumentViewerDbContext(optionsBuilder.Options);
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.First(static c => !string.IsNullOrWhiteSpace(c))!;
}
