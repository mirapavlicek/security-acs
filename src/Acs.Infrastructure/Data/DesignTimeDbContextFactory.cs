using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace Acs.Infrastructure.Data;

/// <summary>Design-time factory pro `dotnet ef migrations` (generuje MariaDB migrace bez připojení k DB).</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AcsDbContext>
{
    public AcsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AcsDbContext>()
            .UseMySql(
                "Server=localhost;Database=winpak;User=winpak;Password=design-time",
                ServerVersion.Create(new Version(10, 6, 0), ServerType.MariaDb))
            .Options;
        return new AcsDbContext(options);
    }
}
