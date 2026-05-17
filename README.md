# rbinilspy

[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Rust](https://img.shields.io/badge/rust-stable-blue)](rust-toolchain.toml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

MCP server for ILSpy-based .NET assembly analysis.

`rbinilspy` manages persistent ILSpy sessions with an in-memory C# worker process,
loads .NET assemblies for analysis, and exposes 17 tools as named
[Model Context Protocol](https://modelcontextprotocol.io) tools over stdio.

## Tools

**Session Management**
- `dn_open` / `dn_close` / `dn_sessions`  --  open/close/list persistent ILSpy sessions
- `dn_metadata`  --  assembly metadata: types count, namespaces count

**Discovery & Navigation**
- `dn_namespaces`  --  list namespaces with type counts, filter, and pagination
- `dn_types`  --  list types with name/namespace filter and pagination
- `dn_search`  --  search types or cached member names by substring
- `dn_type_info`  --  type metadata: base types, interfaces, attributes, modifiers

**Decompilation**
- `dn_decompile`  --  full type decompilation to C# (with result caching)
- `dn_decompile_method`  --  decompile a single method (token-efficient, no full type dump)
- `dn_members`  --  list methods/properties/fields (with result caching)
- `dn_il`  --  raw CIL disassembly
- `dn_search_source`  --  grep decompiled C# source for a pattern

**Assembly Analysis**
- `dn_references`  --  external assembly dependencies
- `dn_usages`  --  find usages of a pattern across decompiled types
- `dn_resources`  --  list embedded resources
- `dn_raw_cmd`  --  passthrough for arbitrary ilspycmd invocations

## Requirements

- **.NET 10 SDK** (for worker build)
- **ILSpy CLI** -- `dotnet tool install -g ilspycmd`
- **Rust stable** toolchain (for building the MCP server)

## Quick Start

```bash
# Build the C# worker
cd ilspy-worker && dotnet publish -c Release -o bin/publish

# Build and run the MCP server
cd .. && cargo build --workspace
RBM_CACHE_DIR=./cache cargo run -p rbm-server
```

The server speaks the MCP protocol over stdio. Configure your MCP client:

```json
{
  "mcpServers": {
    "rbinilspy": {
      "command": "/path/to/rbinilspy/target/debug/rbinilspy",
      "args": []
    }
  }
}
```

## Configuration

| Variable | Default | Description |
| --- | --- | --- |
| `RBM_CACHE_DIR` | `./rbinilspy-cache` | Cache root |
| `RBM_TOOL_TIMEOUT_SECS` | 30 | Per-tool timeout (seconds) |
| `RBM_ILSPY_WORKER` | auto‑detected | Path to the C# worker (binary or project dir) |
| `RBM_ILSPY_CMD` | `ilspycmd` | Path to the ILSpy CLI executable |
| `RBM_ILSPY_CMD_TIMEOUT_MS` | 60000 | ilspycmd subprocess timeout (milliseconds) |

## Architecture

```
MCP Client
  -> stdio JSON-RPC
  -> rbinilspy server (Rust)
  -> C# worker process (stdin/stdout JSON-RPC)
  -> ilspycmd subprocess (per-decompilation)
```

Assemblies are opened once and cached in the C# worker's memory. Decompiled
output and member listings are cached per type for the session lifetime.

## Project Structure

```
crates/
  rbm-core/      Cache paths, config, error types
  rbm-server/    MCP server binary (rbinilspy)
ilspy-worker/    C# persistent worker for ilspycmd
```

## License

MIT  --  see [LICENSE](LICENSE).
