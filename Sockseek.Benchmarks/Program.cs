using BenchmarkDotNet.Running;
using Sockseek.Benchmarks;

if (args.Length > 0 && args[0].Equals("configuration", StringComparison.OrdinalIgnoreCase))
{
    BenchmarkSwitcher.FromTypes([typeof(ConfigurationBenchmarks)]).Run(args[1..]);
    return;
}

if (args.Length > 0 && args[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
{
    await RealSearchCapture.RunAsync(args[1..]);
    return;
}

// Run matching benchmarks without discovering optional real-capture datasets.
if (args.Length > 0 && args[0].Equals("matching", StringComparison.OrdinalIgnoreCase))
{
    BenchmarkSwitcher.FromTypes([typeof(MultiArtistMatchingBenchmarks)]).Run(args[1..]);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(ConfigurationBenchmarks).Assembly).Run(args);
