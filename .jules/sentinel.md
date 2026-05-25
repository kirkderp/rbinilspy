## 2025-05-25 - CLI Argument Blocklist Bypass (Windows/Irregular Prefixes)
**Vulnerability:** A blocklist intended to prevent writing to disk or executing dangerous arguments in `ilspy-worker` via `raw_cmd` only checked for exact strings like `"-o"` or `"--outputdir"`. This could be bypassed using Windows-style slash prefixes (`"/o"`) or irregular combinations (`"--o"`), which CLI parsers often accept.
**Learning:** Checking for exact string matches against command-line arguments is insufficient because underlying parsers (like `System.CommandLine`) typically normalize prefixes and aliases.
**Prevention:** Always normalize command-line arguments by stripping leading `-` and `/` characters before validating them against a blocklist of core option names.
