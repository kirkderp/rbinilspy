## 2024-05-24 - Cache Misses in Expensive Subprocess Calls
**Learning:** Operations that analyze source code (`ListMembers`, `TypeInfo`) were independently triggering expensive decompilation subprocesses (`ilspycmd -t <type>`) without checking or populating the central `Decompiled` cache. This led to redundant subprocess executions for the same type.
**Action:** Always ensure that multi-purpose artifacts (like full decompiled source) are fetched through a centralized caching mechanism before performing downstream analysis, to avoid duplicating expensive OS-level subprocess calls.
