using BenchmarkDotNet.Running;
using Sockseek.Benchmarks;

if (args.Length > 0 && args[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
{
    await RealSearchCapture.RunAsync(args[1..]);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
