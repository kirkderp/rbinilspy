## 2024-05-24 - Zero-allocation string processing in C#
**Learning:** Using `string.Split('\n')` and `.ToLowerInvariant()` inside search loops for decompiled code allocates a large array and creates many new strings, putting significant pressure on the garbage collector and increasing memory usage in the worker.
**Action:** Use `.AsSpan().EnumerateLines()` along with `StringComparison.OrdinalIgnoreCase` to iterate through strings with zero allocations when searching decompiled sources.
