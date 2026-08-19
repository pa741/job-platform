using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JobPlatform.Data.Sql;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without a live database.
/// Migrations are generated from the model, so the connection string here is never used
/// to connect — deliberately a placeholder so no real server name is committed.
/// </summary>
public sealed class DesignTimeJobsDbContextFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlServer("Server=(localdb)\\design-time-only;Database=jobsdb;")
            .Options;

        return new JobsDbContext(options);
    }
}
