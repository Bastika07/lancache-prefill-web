namespace LancachePrefill.Web;

/// <summary>Environment configuration (spec → Deployment), validated once at startup.</summary>
public sealed record AppConfiguration(string AdminPassword, string DataDir, string? LancacheIp)
{
    public static AppConfiguration From(IConfiguration configuration)
    {
        var password = configuration["ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "ADMIN_PASSWORD is not set. Refusing to start without a password for the web UI.");
        }
        var dataDir = configuration["DATA_DIR"] is { Length: > 0 } dir ? dir : "/data";
        Directory.CreateDirectory(dataDir);
        var lancacheIp = configuration["LANCACHE_IP"] is { Length: > 0 } ip ? ip : null;
        return new AppConfiguration(password, dataDir, lancacheIp);
    }
}
