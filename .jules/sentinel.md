## 2025-05-27 - Command Injection Blocklist Bypass via Concatenated Arguments
**Vulnerability:** Command injection blocklist bypass in `ilspy-worker/Program.cs`'s `RunRawCmd` method.
**Learning:** The validation logic only checked for exact matches or options followed by `=` or `:`. It missed scenarios where the option value is concatenated using other non-alphanumeric characters, like `-o/tmp` or `-o"/tmp"`, which could bypass the blocklist while still being parsed as valid options by the underlying tool.
**Prevention:** Use robust boundary checks when validating command-line arguments. Instead of exact matching, verify if the character immediately following the matched flag is non-alphanumeric (e.g., using `!char.IsLetterOrDigit(nextChar)`).
