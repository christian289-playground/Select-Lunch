using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SelectLunch.Shared.Data;

/// <summary>`dotnet ef` 전용. 런타임에는 쓰이지 않는다.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LunchDbContext>
{
    public LunchDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new LunchDbContext(options);
    }
}
