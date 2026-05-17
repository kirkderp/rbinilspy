// rbinilspy MCP server — delegates to C# worker via JSON-RPC over stdio.
// Tool calls are serialized through a tokio::sync::Mutex on the worker handle.
// One request per MCP connection is the reliable pattern.

use std::sync::Arc;

use rmcp::handler::server::ServerHandler;
use rmcp::model::{
    CallToolRequestParams, CallToolResult, Content, ErrorData, Implementation, ListToolsResult,
    PaginatedRequestParams, ServerCapabilities, ServerInfo, Tool,
};
use rmcp::service::{RequestContext, RoleServer};
use serde_json::{json, Value};
use std::process::Stdio;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::process::Child;
use tokio::sync::Mutex;

pub mod support;

#[derive(Clone)]
pub struct RbmServer {
    worker: Arc<Mutex<WorkerHandle>>,
    tools: Vec<Tool>,
}

struct WorkerHandle {
    stdin: tokio::process::ChildStdin,
    stdout: BufReader<tokio::process::ChildStdout>,
    _child: Child,
}

impl RbmServer {
    /// Create a new rbinilspy MCP server, spawning the C# worker subprocess.
    ///
    /// # Errors
    ///
    /// Returns an error if the worker binary cannot be found, spawned, or if the
    /// initial ping to the worker fails.
    pub async fn new() -> Result<Self, ErrorData> {
        let worker_path = std::env::var("RBM_ILSPY_WORKER").unwrap_or_else(|_| {
            let crate_dir = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"));
            let published = crate_dir.join("../../ilspy-worker/bin/publish/ilspy-worker");
            if published.exists() {
                published.to_string_lossy().to_string()
            } else {
                crate_dir
                    .join("../../ilspy-worker")
                    .to_string_lossy()
                    .to_string()
            }
        });

        tracing::info!(%worker_path, "starting ilspy worker");

        let (mut cmd, _is_published) =
            if worker_path.ends_with("ilspy-worker") && !worker_path.ends_with("ilspy-worker/") {
                let mut c = tokio::process::Command::new(&worker_path);
                c.stdin(Stdio::piped())
                    .stdout(Stdio::piped())
                    .stderr(Stdio::inherit());
                (c, true)
            } else {
                let mut c = tokio::process::Command::new("dotnet");
                c.args(["run", "--project", &worker_path]);
                c.stdin(Stdio::piped())
                    .stdout(Stdio::piped())
                    .stderr(Stdio::inherit());
                (c, false)
            };

        let mut child = cmd
            .spawn()
            .map_err(|e| err(format!("failed to spawn worker: {e}")))?;
        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| err("no stdin on worker"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| err("no stdout on worker"))?;

        let mut handle = WorkerHandle {
            stdin,
            stdout: BufReader::new(stdout),
            _child: child,
        };

        // Ping to verify worker is alive
        call_worker_raw(&mut handle, "ping", json!({}))
            .await
            .map_err(|e| err(format!("worker ping failed: {e}")))?;

        tracing::info!("ilspy worker started successfully");

        Ok(Self {
            worker: Arc::new(Mutex::new(handle)),
            tools: Self::build_tools(),
        })
    }

    fn build_tools() -> Vec<Tool> {
        fn t(
            name: &'static str,
            desc: &'static str,
            props: &[(&str, Value)],
            required: &[&str],
        ) -> Tool {
            let mut p = serde_json::Map::new();
            for (k, v) in props.iter().cloned() {
                p.insert(k.to_string(), v);
            }
            let schema = json!({
                "type": "object",
                "properties": p,
                "required": required,
                "additionalProperties": false
            });
            let input_schema = schema.as_object().cloned().unwrap_or_default();
            Tool::new(name, desc, std::sync::Arc::new(input_schema))
        }
        fn req(name: &str) -> Value {
            json!({"type": "string", "description": name})
        }
        fn opt_s(desc: &str) -> Value {
            json!({"type": "string", "description": desc})
        }
        fn opt_u32(desc: &str, def: u32) -> Value {
            json!({"type": "integer", "format": "uint32", "description": desc, "default": def})
        }

        let session_only = &[("session_id", req("session identifier"))];
        let session_required = &["session_id"];

        let mut tools = vec![
            t(
                "dn_open",
                "Load a .NET assembly and start a persistent ILSpy session.",
                &[
                    ("binary_path", req("absolute path to the .NET assembly")),
                    (
                        "session_id",
                        opt_s("optional session identifier; defaults to filename"),
                    ),
                ],
                &["binary_path"],
            ),
            t(
                "dn_close",
                "Close an assembly session and free resources.",
                session_only,
                session_required,
            ),
            t("dn_sessions", "List all open assembly sessions.", &[], &[]),
            t(
                "dn_metadata",
                "Return assembly metadata (types count, version info).",
                session_only,
                session_required,
            ),
        ];

        tools.extend(Self::type_tools(&t, &req, &opt_s, &opt_u32));
        tools.extend(Self::analysis_tools(
            &t,
            &req,
            session_only,
            session_required,
        ));
        tools
    }

    fn type_tools(
        t: &impl Fn(&'static str, &'static str, &[(&str, Value)], &[&str]) -> Tool,
        req: &impl Fn(&str) -> Value,
        opt_s: &impl Fn(&str) -> Value,
        opt_u32: &impl Fn(&str, u32) -> Value,
    ) -> Vec<Tool> {
        vec![
            t(
                "dn_types",
                "List types with optional name/namespace filter and pagination.",
                &[
                    ("session_id", req("session identifier")),
                    ("filter", opt_s("optional name filter (substring)")),
                    ("namespace", opt_s("optional exact namespace filter")),
                    ("offset", opt_u32("result offset", 0)),
                    ("limit", opt_u32("max results; 0 returns all", 0)),
                ],
                &["session_id"],
            ),
            t(
                "dn_decompile",
                "Decompile a type to C# source code.",
                &[
                    ("session_id", req("session identifier")),
                    ("type_name", req("fully qualified type name")),
                ],
                &["session_id", "type_name"],
            ),
            t(
                "dn_search",
                "Search types or member names by substring. entity=type (default) searches type names; entity=member searches cached member/method/property names.",
                &[
                    ("session_id", req("session identifier")),
                    ("pattern", req("search pattern")),
                    ("entity", opt_s("search entity: type (default) or member")),
                    ("limit", opt_u32("max results", 20)),
                ],
                &["session_id", "pattern"],
            ),
            t(
                "dn_type_info",
                "Show type metadata: base types, interfaces, attributes, modifiers.",
                &[
                    ("session_id", req("session identifier")),
                    ("type_name", req("fully qualified type name")),
                ],
                &["session_id", "type_name"],
            ),
            t(
                "dn_members",
                "List methods, properties, and fields of a type (token-efficient, no full decompile).",
                &[
                    ("session_id", req("session identifier")),
                    ("type_name", req("fully qualified type name")),
                ],
                &["session_id", "type_name"],
            ),
            t(
                "dn_references",
                "List assembly references (dependent assemblies).",
                &[("session_id", req("session identifier"))],
                &["session_id"],
            ),
            t(
                "dn_namespaces",
                "List namespaces with type counts, filter, pagination.",
                &[
                    ("session_id", req("session identifier")),
                    ("filter", opt_s("optional namespace filter (substring)")),
                    ("offset", opt_u32("result offset", 0)),
                    ("limit", opt_u32("max results; 0 returns all", 0)),
                ],
                &["session_id"],
            ),
        ]
    }

    fn analysis_tools(
        t: &impl Fn(&'static str, &'static str, &[(&str, Value)], &[&str]) -> Tool,
        req: &impl Fn(&str) -> Value,
        session_only: &[(&str, Value)],
        session_required: &[&str],
    ) -> Vec<Tool> {
        vec![
            t(
                "dn_decompile_method",
                "Decompile a single method from a type (token-efficient, no full type dump).",
                &[
                    ("session_id", req("session identifier")),
                    ("type_name", req("fully qualified type name")),
                    ("method_name", req("method name to decompile")),
                ],
                &["session_id", "type_name", "method_name"],
            ),
            t(
                "dn_search_source",
                "Search decompiled C# source of a type for a pattern (grep). Uses cached decompile when available.",
                &[
                    ("session_id", req("session identifier")),
                    ("type_name", req("fully qualified type name")),
                    (
                        "pattern",
                        req("search pattern (case-insensitive substring)"),
                    ),
                ],
                &["session_id", "type_name", "pattern"],
            ),
            t(
                "dn_raw_cmd",
                "Run arbitrary ilspycmd args passthrough.",
                &[
                    ("session_id", req("session identifier")),
                    ("args", req("ilspycmd arguments")),
                ],
                &["session_id", "args"],
            ),
            t(
                "dn_usages",
                "Find usages of a pattern across all decompiled types.",
                &[
                    ("session_id", req("session identifier")),
                    ("pattern", req("pattern to search for")),
                ],
                &["session_id", "pattern"],
            ),
            t(
                "dn_resources",
                "List embedded resources.",
                session_only,
                session_required,
            ),
            t(
                "dn_il",
                "Show IL code for a type.",
                &[
                    ("session_id", req("session identifier")),
                    ("type_name", req("fully qualified type name")),
                ],
                &["session_id", "type_name"],
            ),
        ]
    }
}

impl ServerHandler for RbmServer {
    fn get_info(&self) -> ServerInfo {
        ServerInfo::new(ServerCapabilities::builder().enable_tools().build())
            .with_server_info(Implementation::new("rbinilspy", env!("CARGO_PKG_VERSION")))
            .with_instructions("rbinilspy ILSpy MCP server — 17 tools for .NET binary analysis.")
    }

    async fn list_tools(
        &self,
        _: Option<PaginatedRequestParams>,
        _: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, ErrorData> {
        Ok(ListToolsResult {
            tools: self.tools.clone(),
            meta: None,
            next_cursor: None,
        })
    }

    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _: RequestContext<RoleServer>,
    ) -> Result<CallToolResult, ErrorData> {
        let name = request.name.as_ref();
        let params = request.arguments.unwrap_or_default();

        let (method, worker_params) = Self::build_call(name, &Value::Object(params.clone()))?;

        let response = {
            let mut guard = self.worker.lock().await;
            call_worker_raw(&mut guard, method, worker_params).await
        }
        .map_err(|e| ErrorData::new(rmcp::model::ErrorCode::INTERNAL_ERROR, e, None))?;

        // Unwrap the inner result from the worker's JSON-RPC response envelope
        let inner = response.get("result").ok_or_else(|| {
            ErrorData::new(
                rmcp::model::ErrorCode::INTERNAL_ERROR,
                "worker response missing result field",
                None,
            )
        })?;

        Ok(CallToolResult::success(vec![Content::text(
            inner.to_string(),
        )]))
    }
}

impl RbmServer {
    fn build_call(name: &str, params: &Value) -> Result<(&'static str, Value), ErrorData> {
        match name {
            "dn_open" => Ok(Self::session_call(
                "open",
                json!({"assembly_path": str_param(params, "binary_path")}),
                params,
            )),
            "dn_close" => Ok(Self::session_call("close", json!({}), params)),
            "dn_sessions" => Ok(("sessions", json!({}))),
            "dn_metadata" => Ok(Self::session_call("metadata", json!({}), params)),
            "dn_types" => Ok((
                "types",
                json!({
                    "session_id": str_param(params, "session_id"),
                    "filter": str_param(params, "filter"),
                    "namespace": str_param(params, "namespace"),
                    "offset": int_param(params, "offset", 0),
                    "limit": int_param(params, "limit", 0),
                }),
            )),
            "dn_decompile" => Ok(Self::type_call("decompile", params)),
            "dn_search" => Ok((
                "search",
                json!({
                    "session_id": str_param(params, "session_id"),
                    "pattern": str_param(params, "pattern"),
                    "entity": str_param(params, "entity"),
                    "limit": int_param(params, "limit", 20),
                }),
            )),
            "dn_type_info" => Ok(Self::type_call("typeinfo", params)),
            "dn_members" => Ok(Self::type_call("members", params)),
            "dn_references" => Ok(Self::session_call("references", json!({}), params)),
            "dn_namespaces" => Ok((
                "namespaces",
                json!({
                    "session_id": str_param(params, "session_id"),
                    "filter": str_param(params, "filter"),
                    "offset": int_param(params, "offset", 0),
                    "limit": int_param(params, "limit", 0),
                }),
            )),
            "dn_decompile_method" => Ok((
                "method_source",
                json!({
                    "session_id": str_param(params, "session_id"),
                    "type_name": str_param(params, "type_name"),
                    "method_name": str_param(params, "method_name"),
                }),
            )),
            "dn_search_source" => Ok((
                "source_search",
                json!({
                    "session_id": str_param(params, "session_id"),
                    "type_name": str_param(params, "type_name"),
                    "pattern": str_param(params, "pattern"),
                }),
            )),
            "dn_raw_cmd" => Ok((
                "raw_cmd",
                json!({
                    "session_id": str_param(params, "session_id"),
                    "args": str_param(params, "args"),
                }),
            )),
            "dn_usages" => Ok((
                "find_usages",
                json!({
                    "session_id": str_param(params, "session_id"),
                    "pattern": str_param(params, "pattern"),
                }),
            )),
            "dn_resources" => Ok(Self::session_call("resources", json!({}), params)),
            "dn_il" => Ok(Self::type_call("il", params)),
            _ => Err(ErrorData::new(
                rmcp::model::ErrorCode::INVALID_PARAMS,
                format!("unknown tool: {name}"),
                None,
            )),
        }
    }

    fn session_call(
        method: &'static str,
        mut params: Value,
        request: &Value,
    ) -> (&'static str, Value) {
        if let Value::Object(ref mut object) = params {
            object.insert(
                "session_id".to_string(),
                Value::String(str_param(request, "session_id")),
            );
        }
        (method, params)
    }

    fn type_call(method: &'static str, request: &Value) -> (&'static str, Value) {
        Self::session_call(
            method,
            json!({"type_name": str_param(request, "type_name")}),
            request,
        )
    }
}

fn str_param(params: &Value, key: &str) -> String {
    params
        .get(key)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string()
}

fn int_param(params: &Value, key: &str, default: u64) -> u64 {
    params.get(key).and_then(Value::as_u64).unwrap_or(default)
}

async fn call_worker_raw(
    handle: &mut WorkerHandle,
    method: &str,
    params: Value,
) -> Result<Value, String> {
    let request = json!({
        "id": 1,
        "method": method,
        "params": params,
    });

    let mut req_line = request.to_string();
    req_line.push('\n');
    handle
        .stdin
        .write_all(req_line.as_bytes())
        .await
        .map_err(|e| format!("write to worker: {e}"))?;
    handle
        .stdin
        .flush()
        .await
        .map_err(|e| format!("flush worker: {e}"))?;

    let mut line = String::new();
    handle
        .stdout
        .read_line(&mut line)
        .await
        .map_err(|e| format!("read from worker: {e}"))?;

    let parsed: Value = serde_json::from_str(&line)
        .map_err(|e| format!("parse worker response: {e} (raw: {line:?})"))?;

    // Check for worker error responses
    if let Some(error_msg) = parsed.get("error").and_then(|v| v.as_str()) {
        return Err(format!("worker error: {error_msg}"));
    }

    Ok(parsed)
}

fn err(msg: impl Into<String>) -> ErrorData {
    ErrorData::new(rmcp::model::ErrorCode::INVALID_PARAMS, msg.into(), None)
}
