use std::fmt;
use std::path::PathBuf;

use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
pub enum ToolError {
    /// Invalid input from the client (e.g., bad address, missing parameter).
    Invalid(String),
    /// Backend tool failure (e.g., r2 error, ilspy error).
    Backend(String, String),
    /// I/O error (e.g., file not found).
    Io(PathBuf, String),
    /// Feature not implemented.
    NotImplemented(String),
    /// Resource not found.
    NotFound(String),
}

pub type ToolResult<T> = Result<T, ToolError>;

impl fmt::Display for ToolError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Invalid(msg) => write!(f, "invalid input: {msg}"),
            Self::Backend(tool, msg) => write!(f, "backend error ({tool}): {msg}"),
            Self::Io(path, msg) => write!(f, "I/O error for {}: {msg}", path.display()),
            Self::NotImplemented(feature) => write!(f, "not implemented: {feature}"),
            Self::NotFound(resource) => write!(f, "not found: {resource}"),
        }
    }
}

impl ToolError {
    #[must_use]
    pub fn invalid(msg: impl Into<String>) -> Self {
        Self::Invalid(msg.into())
    }

    #[must_use]
    pub fn backend(tool: impl Into<String>, msg: impl Into<String>) -> Self {
        Self::Backend(tool.into(), msg.into())
    }

    #[must_use]
    pub fn io(path: PathBuf, err: impl std::error::Error) -> Self {
        Self::Io(path, err.to_string())
    }

    #[must_use]
    pub fn not_found(resource: impl Into<String>) -> Self {
        Self::NotFound(resource.into())
    }
}
