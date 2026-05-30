## 2024-05-30 - Fix blocklist bypass in RunRawCmd
**Vulnerability:** Command-line argument blocklist in `RunRawCmd` could be bypassed using concatenated short options (e.g., `-o/tmp` instead of `-o /tmp`).
**Learning:** Exact string matching or matching only specific delimiters (`=`, `:`) is insufficient for command-line argument validation, as parsers often accept values concatenated directly to short flags.
**Prevention:** Use robust boundary checks (e.g., verifying that the character immediately following a matched prefix is non-alphanumeric) instead of exact matching or limited delimiter checks.
