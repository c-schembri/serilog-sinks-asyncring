using AsyncRingBenchmarks;
using BenchmarkDotNet.Running;

// dotnet run -c Release -f net10.0 -- --filter '*'             everything, on .NET 8 and .NET 10
// dotnet run -c Release -f net10.0 -- --filter '*LogCall*'     one benchmark class
// BENCH_RUNTIMES=net10.0 dotnet run ...                         only some runtimes
// (-f only picks the runtime that hosts BenchmarkDotNet; see --help for BenchmarkDotNet's options)

var statsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts", "stats");
if (Directory.Exists(statsDirectory)) Directory.Delete(statsDirectory, recursive: true);
Directory.CreateDirectory(statsDirectory);
Environment.SetEnvironmentVariable(BenchmarkStats.DirectoryVariable, statsDirectory);

BenchmarkSwitcher.FromAssembly(typeof(LoggingBenchmark).Assembly).Run(args);
