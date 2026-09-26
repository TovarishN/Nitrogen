using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Nitrogen.Benchmarks.MotionParseBenchmarks).Assembly).Run(args);
