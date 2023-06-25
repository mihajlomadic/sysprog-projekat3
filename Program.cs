using Microsoft.Extensions.Configuration;
using Reactive;
using System.Reactive.Concurrency;

object _consoleWriteLock = new();

IConfiguration configuration = new ConfigurationBuilder()
    .AddJsonFile("./appsettings.json", optional: true, reloadOnChange: true)
    .Build();

string? pat = configuration["GitHub:PAT"];

if (pat == null)
    throw new ArgumentNullException("GitHub PAT not provided. Create appsettings.json!");

string[] prefixes = new[] { "http://localhost:8080/", "http://127.0.0.1:8080/" };

ReactiveHttpServer server = new(pat, 100, prefixes, TaskPoolScheduler.Default);

server.Launch();

Console.WriteLine("Press any key to exit...");

Console.ReadKey();

server.Terminate();