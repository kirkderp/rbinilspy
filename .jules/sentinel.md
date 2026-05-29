## 2024-05-15 - Blocklist Bypass via Concatenated Command-Line Options
**Vulnerability:** Command injection / blocklist bypass in `RunRawCmd` allowing users to pass dangerous arguments like `-o/tmp` or `-o"/tmp"`.
**Learning:** Checking command-line arguments using exact equality or explicit delimiters (like `=` or `:`) is insufficient because short options can be concatenated with their values (e.g., `-o/tmp`).
**Prevention:** Validate command-line arguments using robust boundary checks. Verify that the character immediately following the matched flag is non-alphanumeric instead of relying exclusively on exact string matching or `=` / `:` delimiters.
