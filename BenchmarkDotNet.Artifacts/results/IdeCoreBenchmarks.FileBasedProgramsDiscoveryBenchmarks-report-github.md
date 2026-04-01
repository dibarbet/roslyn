``` ini

BenchmarkDotNet=v0.13.0, OS=Windows 10.0.26200
Intel Core i9-10900K CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK=10.0.105
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT


```
|                          Method |      WorkspaceFolder |     Mean |   Error |  StdDev |     Gen 0 |    Gen 1 | Gen 2 | Allocated |
|-------------------------------- |--------------------- |---------:|--------:|--------:|----------:|---------:|------:|----------:|
|     &#39;Cached (incremental walk)&#39; | C:\Us(...)oslyn [37] | 308.0 ms | 5.98 ms | 8.19 ms |         - |        - |     - |      4 MB |
| &#39;Parallel full walk (no cache)&#39; | C:\Us(...)oslyn [37] | 186.9 ms | 3.71 ms | 5.33 ms | 2000.0000 | 666.6667 |     - |     20 MB |
