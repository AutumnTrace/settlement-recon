```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7623/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 9800X3D 4.70GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-UWLSOM : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

IterationCount=10  LaunchCount=1  WarmupCount=3  

```
| Method                                    | Transactions | Mean        | Error      | StdDev     | Ratio         | RatioSD | Txn/s     | Gen0       | Gen1       | Gen2      | Allocated  | Alloc Ratio |
|------------------------------------------ |------------- |------------:|-----------:|-----------:|--------------:|--------:|----------:|-----------:|-----------:|----------:|-----------:|------------:|
| **&#39;Naive (ReadAllLines + Split + LINQ)&#39;**     | **100000**       |   **319.10 ms** |  **25.088 ms** |  **14.930 ms** |      **baseline** |        **** |   **313,381** | **12000.0000** |  **8000.0000** | **3000.0000** |  **310.11 MB** |            **** |
| &#39;Pipeline (Pipelines + spans + channels)&#39; | 100000       |    16.52 ms |   0.734 ms |   0.485 ms | 19.33x faster |   1.01x | 6,054,481 |   656.2500 |   625.0000 |  625.0000 |   18.98 MB | 16.34x less |
|                                           |              |             |            |            |               |         |           |            |            |           |            |             |
| **&#39;Naive (ReadAllLines + Split + LINQ)&#39;**     | **1000000**      | **2,615.93 ms** | **186.935 ms** | **123.646 ms** |      **baseline** |        **** |   **382,273** | **63000.0000** | **37000.0000** | **3000.0000** | **3083.12 MB** |            **** |
| &#39;Pipeline (Pipelines + spans + channels)&#39; | 1000000      |   215.10 ms |  16.282 ms |  10.769 ms | 12.19x faster |   0.79x | 4,649,073 |  1500.0000 |  1500.0000 | 1500.0000 |  113.76 MB | 27.10x less |
