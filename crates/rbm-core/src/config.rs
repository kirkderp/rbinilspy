use std::time::Duration;

use crate::env::parse_env_secs;
use crate::paths::CachePaths;

#[derive(Debug, Clone)]
pub struct ServerConfig {
    pub cache: CachePaths,
    pub tool_timeout: Duration,
    pub ilspy_worker_path: String,
}

impl ServerConfig {
    /// Build server configuration from process environment variables.
    ///
    /// # Errors
    ///
    /// Returns an error if cache path discovery fails.
    pub fn from_env() -> crate::ToolResult<Self> {
        let cache = CachePaths::from_env()?;
        let manifest = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"));
        let default_worker = manifest
            .join("../../ilspy-worker")
            .canonicalize()
            .map_or_else(
                |_| "rbinilspy-worker".to_string(),
                |p| p.join("rbinilspy-worker").to_string_lossy().to_string(),
            );

        Ok(Self {
            cache,
            tool_timeout: Duration::from_secs(parse_env_secs("RBM_TOOL_TIMEOUT_SECS", 30)),
            ilspy_worker_path: std::env::var("RBM_ILSPY_WORKER").unwrap_or(default_worker),
        })
    }
}
