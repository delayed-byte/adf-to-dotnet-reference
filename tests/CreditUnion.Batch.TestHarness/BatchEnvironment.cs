using System.Diagnostics;
using System.Globalization;
using CreditUnion.Batch.TestHarness.Db2;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using CreditUnion.Batch.TestHarness.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Testcontainers.Azurite;
using Testcontainers.Db2;
using Testcontainers.MsSql;

namespace CreditUnion.Batch.TestHarness;

/// <summary>
/// The local stand-ins for KeyStone Db2, the reporting store and Blob Storage, started once per test
/// run (the Db2 container takes one to three minutes) and reset between scenarios (PRD, Acceptance
/// tests). Image tags match docker-compose.yml. Passwords are generated per run.
/// <para>
/// Set <c>CU_TESTS_USE_COMPOSE=1</c> to reuse the stack from <c>./build.ps1 up</c> instead (faster local
/// loops). The tests then deploy into it and wipe its scenario data; passwords come from .env.
/// </para>
/// </summary>
public sealed class BatchEnvironment : IAsyncDisposable
{
    public const string Db2Image = "icr.io/db2_community/db2:12.1.0.0";
    public const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    public const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.33.0";
    public const string UseComposeVariable = "CU_TESTS_USE_COMPOSE";
    private const string Db2Database = "KEYSTONE";
    private const string ReportingDatabaseName = "Reporting";

    private readonly IReadOnlyList<IContainer> containers;

    private BatchEnvironment(TestSecrets secrets, KeyStoneDb2 keyStone, ReportingDatabase reporting, BlobStore blobs, IReadOnlyList<IContainer> containers, TimeSpan startup)
    {
        Secrets = secrets;
        KeyStone = keyStone;
        Reporting = reporting;
        Blobs = blobs;
        this.containers = containers;
        StartupTime = startup;
    }

    public TestSecrets Secrets { get; }

    public KeyStoneDb2 KeyStone { get; }

    public ReportingDatabase Reporting { get; }

    public BlobStore Blobs { get; }

    public TimeSpan StartupTime { get; }

    public bool UsesComposeStack => containers.Count == 0;

    public static async Task<BatchEnvironment> StartAsync(CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var environment = Environment.GetEnvironmentVariable(UseComposeVariable) == "1"
            ? FromComposeStack(clock)
            : await StartContainersAsync(clock, cancellationToken).ConfigureAwait(false);

        environment.KeyStone.Deploy();
        environment.Reporting.Deploy(environment.Secrets);
        await environment.ResetAsync(cancellationToken).ConfigureAwait(false);
        return environment;
    }

    /// <summary>Clears every scenario's data from Db2, the reporting store and blob storage.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await KeyStone.ResetAsync(cancellationToken).ConfigureAwait(false);
        await Reporting.ResetAsync(cancellationToken).ConfigureAwait(false);
        await Blobs.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets, then loads the scenario's Db2 rows and landing files.</summary>
    public async Task LoadAsync(Scenario scenario, CancellationToken cancellationToken = default)
    {
        await ResetAsync(cancellationToken).ConfigureAwait(false);
        await KeyStone.LoadAsync(scenario, cancellationToken).ConfigureAwait(false);
        await Blobs.LoadAsync(scenario, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in containers)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<BatchEnvironment> StartContainersAsync(Stopwatch clock, CancellationToken cancellationToken)
    {
        var secrets = TestSecrets.Generate();
        var db2 = new Db2Builder(Db2Image)
            .WithAcceptLicenseAgreement(true)
            .WithDatabase(Db2Database)
            .WithPassword(secrets.Db2AdminPassword)
            .WithEnvironment("UPDATEAVAIL", "NO")
            .WithEnvironment("KS_EXTRACT_PASSWORD", secrets.Db2ExtractPassword)
            .WithResourceMapping(await File.ReadAllBytesAsync(RepositoryPaths.Db2ExtractUserScript, cancellationToken).ConfigureAwait(false),
                "/var/custom/create_extract_user.sh", fileMode: Unix.FileMode755)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("id", "ksextract"))
            .Build();
        var sql = new MsSqlBuilder(SqlServerImage).WithPassword(secrets.SqlAdminPassword).Build();
        var azurite = new AzuriteBuilder(AzuriteImage).WithInMemoryPersistence().WithCommand("--skipApiVersionCheck").Build();

        IReadOnlyList<IContainer> all = [db2, sql, azurite];
        await Task.WhenAll(all.Select(c => c.StartAsync(cancellationToken))).ConfigureAwait(false);

        var db2Port = db2.GetMappedPublicPort(Db2Builder.Db2Port);
        var sqlAdmin = new SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = ReportingDatabaseName, Encrypt = true, TrustServerCertificate = true };
        return Create(secrets, db2.Hostname, db2Port, sqlAdmin, azurite.GetConnectionString(), all, clock);
    }

    private static BatchEnvironment FromComposeStack(Stopwatch clock)
    {
        if (!File.Exists(RepositoryPaths.EnvFile))
        {
            throw new InvalidOperationException($"{UseComposeVariable}=1 needs the compose stack and its .env; run ./build.ps1 up first.");
        }

        var env = File.ReadAllLines(RepositoryPaths.EnvFile).Where(l => l.Contains('=', StringComparison.Ordinal))
            .ToDictionary(l => l[..l.IndexOf('=', StringComparison.Ordinal)], l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal);
        var secrets = TestSecrets.Generate() with
        {
            Db2AdminPassword = env["DB2_PASSWORD"],
            Db2ExtractPassword = env["DB2_EXTRACT_PASSWORD"],
            SqlAdminPassword = env["SQL_PASSWORD"],
            SqlWriterPassword = env["SQL_WRITER_PASSWORD"],
            SqlReaderPassword = env["SQL_READER_PASSWORD"],
        };
        var sqlAdmin = new SqlConnectionStringBuilder
        {
            DataSource = "localhost,14330", InitialCatalog = ReportingDatabaseName, UserID = "sa", Password = secrets.SqlAdminPassword,
            Encrypt = true, TrustServerCertificate = true,
        };
        return Create(secrets, "localhost", 50000, sqlAdmin, "UseDevelopmentStorage=true", [], clock);
    }

    private static BatchEnvironment Create(TestSecrets secrets, string db2Host, int db2Port, SqlConnectionStringBuilder sqlAdmin, string blobConnection, IReadOnlyList<IContainer> containers, Stopwatch clock)
    {
        string Db2(string user, string password) => string.Create(CultureInfo.InvariantCulture, $"Server={db2Host}:{db2Port};Database={Db2Database};UID={user};PWD={password};");
        string Sql(string user, string password) => new SqlConnectionStringBuilder(sqlAdmin.ConnectionString) { UserID = user, Password = password }.ConnectionString;

        var keyStone = new KeyStoneDb2(Db2("db2inst1", secrets.Db2AdminPassword), Db2("ksextract", secrets.Db2ExtractPassword), db2Host, db2Port);
        var reporting = new ReportingDatabase(sqlAdmin.ConnectionString, Sql("cu_batch_writer", secrets.SqlWriterPassword), Sql("cu_report_reader", secrets.SqlReaderPassword));
        return new BatchEnvironment(secrets, keyStone, reporting, new BlobStore(blobConnection), containers, clock.Elapsed);
    }
}
