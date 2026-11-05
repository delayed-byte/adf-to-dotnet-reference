using CreditUnion.Batch.DbDeploy;

// Usage: dotnet run --project tools/CreditUnion.Batch.DbDeploy -- [reporting|keystone|all]
// Connection details come from environment variables (set by ./build.ps1 seed from .env):
//   REPORTING_ADMIN_CONNECTION   SQL Server connection string naming the Reporting database
//   REPORTING_WRITER_PASSWORD    password for login cu_batch_writer
//   REPORTING_READER_PASSWORD    password for login cu_report_reader
//   KEYSTONE_ADMIN_CONNECTION    Db2 connection string for the instance owner
//   DB_SCRIPTS_ROOT              optional; defaults to <repo>/db

var target = args.Length > 0 ? args[0].ToUpperInvariant() : "ALL";
if (target is not ("ALL" or "REPORTING" or "KEYSTONE"))
{
    await Console.Error.WriteLineAsync("Usage: DbDeploy [reporting|keystone|all]").ConfigureAwait(false);
    return 2;
}

var scriptsRoot = Environment.GetEnvironmentVariable("DB_SCRIPTS_ROOT") ?? FindDbFolder();

if (target is "ALL" or "KEYSTONE")
{
    Console.WriteLine("Deploying synthetic KeyStone schema to Db2...");
    var result = KeyStoneDb2Deployer.Deploy(Required("KEYSTONE_ADMIN_CONNECTION"), Path.Combine(scriptsRoot, "keystone-db2"));
    if (!result.Successful)
    {
        return 1;
    }
}

if (target is "ALL" or "REPORTING")
{
    Console.WriteLine("Deploying reporting store to SQL Server...");
    var logins = new ReportingLogins(Required("REPORTING_WRITER_PASSWORD"), Required("REPORTING_READER_PASSWORD"));
    var result = ReportingDatabaseDeployer.Deploy(Required("REPORTING_ADMIN_CONNECTION"), Path.Combine(scriptsRoot, "reporting"), logins);
    if (!result.Successful)
    {
        return 1;
    }
}

Console.WriteLine("Database deployment succeeded.");
return 0;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Environment variable {name} is not set. Run ./build.ps1 seed.");

static string FindDbFolder()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AdfToDotnet.sln")))
        {
            return Path.Combine(dir.FullName, "db");
        }
    }

    throw new DirectoryNotFoundException("Could not locate the repository root (AdfToDotnet.sln). Set DB_SCRIPTS_ROOT.");
}
