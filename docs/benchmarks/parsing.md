```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7623/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 9800X3D 4.70GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-UWLSOM : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=10  LaunchCount=1  WarmupCount=3  

```
| Method                                 | Transactions | Mean      | Error     | StdDev    | Ratio        | RatioSD | Gen0      | Gen1      | Gen2      | Allocated    | Alloc Ratio   |
|--------------------------------------- |------------- |----------:|----------:|----------:|-------------:|--------:|----------:|----------:|----------:|-------------:|--------------:|
| &#39;ReadAllLines + Split + decimal.Parse&#39; | 1000000      | 273.57 ms | 30.551 ms | 20.207 ms |     baseline |         | 9500.0000 | 4500.0000 | 1500.0000 | 443980.02 KB |               |
| &#39;PipeReader + spans + pooled batches&#39;  | 1000000      |  73.56 ms |  3.513 ms |  1.837 ms | 3.72x faster |   0.28x |         - |         - |         - |     517.3 KB | 858.266x less |
