using Companion.Cli;
using Companion.Infrastructure;
using Companion.Infrastructure.Persistence;
using Companion.Infrastructure.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var dbPath = builder.Configuration["Database:Path"] ?? "companion.db";

// Maintenance sub-commands run BEFORE the app opens or migrates the database:
//   companion export <file>   → write a consistent snapshot (never loses data)
//   companion import <file>   → replace the DB with a snapshot (backs up the current one first)
var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
if (command is "export" or "import")
{
    var file = args.Where(a => !a.StartsWith('-')).Skip(1).FirstOrDefault();
    if (string.IsNullOrWhiteSpace(file))
    {
        Console.Error.WriteLine($"Usage: companion {command} <file>");
        return;
    }
    try
    {
        if (command == "export")
        {
            DatabaseMaintenance.Export(dbPath, file);
            Console.WriteLine($"Exported a snapshot to {file}.");
        }
        else
        {
            var backup = DatabaseMaintenance.Import(dbPath, file, TimeProvider.System.GetUtcNow());
            Console.WriteLine($"Imported {file} into {dbPath}." +
                (backup is null ? "" : $" Previous database backed up to {backup}."));
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
    }
    return;
}

builder.Services.AddCompanion(builder.Configuration, $"Data Source={dbPath}");

using var host = builder.Build();

// Apply migrations so the schema is created/upgraded before anything runs.
try
{
    await host.Services.MigrateDatabaseAsync();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}

var clock = host.Services.GetRequiredService<TimeProvider>();
const string userId = CompanionSeeder.DemoUserId;

// Sub-command mode: `companion seed` seeds and exits.
var positional = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
if (positional == "seed")
{
    using var scope = host.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<CompanionSeeder>();
    var seeded = await seeder.SeedAsync(clock.GetUtcNow());
    Console.WriteLine(seeded
        ? $"Seeded demo history for '{userId}' into {dbPath}."
        : "Already seeded (skipped).");
    return;
}

// Interactive mode.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var loop = new ChatLoop(host.Services, clock, userId);
try
{
    await loop.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // graceful Ctrl-C
}

Console.WriteLine("Goodbye.");
