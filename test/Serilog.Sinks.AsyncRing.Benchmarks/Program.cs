using AsyncRingBenchmarks;
using BenchmarkDotNet.Running;

// dotnet run -c Release -- --filter '*'                   everything
// dotnet run -c Release -- --filter '*LogCall*'           one benchmark class
// dotnet run -c Release -- --filter '*' --threads ...     see --help for BenchmarkDotNet's options

var statsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts", "delivery");
if (Directory.Exists(statsDirectory)) Directory.Delete(statsDirectory, recursive: true);
Directory.CreateDirectory(statsDirectory);
Environment.SetEnvironmentVariable(DeliveryStats.DirectoryVariable, statsDirectory);

BenchmarkSwitcher.FromAssembly(typeof(LoggingBenchmark).Assembly).Run(args);
