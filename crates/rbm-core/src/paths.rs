use std::path::PathBuf;

use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
pub struct CachePaths {
    pub root: PathBuf,
    pub overflow_dir: PathBuf,
}

impl CachePaths {
    /// Build cache paths from environment, using the default `./rbinilspy-cache`
    /// when `RBM_CACHE_DIR` is not set.
    ///
    /// # Errors
    ///
    /// Returns an error if the cache directory cannot be created.
    pub fn from_env() -> crate::ToolResult<Self> {
        let root = std::env::var("RBM_CACHE_DIR")
            .map_or_else(|_| PathBuf::from("./rbinilspy-cache"), PathBuf::from);
        let overflow_dir = root.join("overflow");
        std::fs::create_dir_all(&overflow_dir)
            .map_err(|e| crate::ToolError::io(overflow_dir.clone(), e))?;
        Ok(Self { root, overflow_dir })
    }
}
