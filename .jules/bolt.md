## 2024-05-28 - Caching Subprocess Output
**Learning:** In backend systems wrapping heavy CLIs (like `ilspycmd`), caching the raw string output of subprocesses is critical for performance. Unnecessary subprocess executions are severe performance bottlenecks compared to memory lookups.
**Action:** Always verify if a subprocess's expected output is already stored in an existing cache (like `CachedAssembly.Decompiled`) before spawning a new process for equivalent operations like `TypeInfo` or `ListMembers`.
