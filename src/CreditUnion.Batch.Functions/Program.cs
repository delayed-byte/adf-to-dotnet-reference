using CreditUnion.Batch.Functions;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) => services.AddCreditUnionBatch(context.Configuration))
    .Build();

await host.RunAsync();
