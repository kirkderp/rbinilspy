use std::fs;
use std::path::PathBuf;
use std::time::{Duration, SystemTime};

use serde::Serialize;

/// Limit for inline text content returned from guarded output helpers.
pub(crate) const GUARDED_INLINE_LIMIT: usize = 24 * 1024;

/// A value that may be returned inline or written to an overflow file.
#[derive(Debug, Clone, Serialize)]
pub struct GuardedOutput {
    pub text: String,
    pub overflow: bool,
    pub file_path: Option<PathBuf>,
}

/// Manages overflow file creation and cleanup for large MCP responses.
pub struct OutputGuard {
    overflow_dir: PathBuf,
    ttl: Duration,
}

impl OutputGuard {
    #[must_use]
    pub const fn new(overflow_dir: PathBuf) -> Self {
        Self {
            overflow_dir,
            ttl: Duration::from_secs(300),
        }
    }

    /// Guard a value by returning it inline if small, otherwise writing to an
    /// overflow file.
    ///
    /// # Errors
    ///
    /// Returns an error if the overflow file cannot be written.
    pub fn guard<T: Serialize>(&self, value: &T) -> std::io::Result<GuardedOutput> {
        let json = serde_json::to_string(value)?;
        Ok(self.guard_str(&json))
    }

    /// Guard a pre-serialized string.
    #[must_use]
    pub fn guard_str(&self, text: &str) -> GuardedOutput {
        if text.len() <= GUARDED_INLINE_LIMIT {
            return GuardedOutput {
                text: text.to_string(),
                overflow: false,
                file_path: None,
            };
        }
        let name = format!(
            "mcp_{}_{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos()),
            nanoid()
        );
        let path = self.overflow_dir.join(name);
        let preview = &text[..text.len().min(200)];
        let preview = preview.to_string();
        let _ = fs::write(&path, text);
        GuardedOutput {
            text: preview,
            overflow: true,
            file_path: Some(path),
        }
    }

    /// Remove overflow files older than the TTL.
    pub fn cleanup(&self) {
        let now = SystemTime::now();
        if let Ok(entries) = fs::read_dir(&self.overflow_dir) {
            for entry in entries.flatten() {
                if let Some(_metadata) = entry
                    .metadata()
                    .and_then(|m| m.modified())
                    .ok()
                    .filter(|m| now.duration_since(*m).is_ok_and(|age| age > self.ttl))
                {
                    let _ = fs::remove_file(entry.path());
                }
            }
        }
    }
}

fn nanoid() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_nanos());
    format!("{nanos:x}")
}
