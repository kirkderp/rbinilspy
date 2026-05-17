use std::path::PathBuf;

/// Look up an environment variable and return its value as a u64, or the default.
pub(crate) fn parse_env_secs(name: &str, default: u64) -> u64 {
    std::env::var(name)
        .ok()
        .and_then(|value| value.parse().ok())
        .unwrap_or(default)
}

/// Look up an environment variable that holds a file-system path.
pub(crate) fn _nonempty_env_path(name: &str) -> Option<PathBuf> {
    let val = std::env::var(name).ok()?;
    if val.is_empty() {
        None
    } else {
        Some(PathBuf::from(val))
    }
}
