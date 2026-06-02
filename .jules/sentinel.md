## 2024-10-27 - [POSIX Argument Validation Bypass]
**Vulnerability:** A command-line injection bypass was possible in `ilspy-worker`'s `raw_cmd` due to improperly handling POSIX-style argument parsing.
**Learning:** Checking for exact string matches (`-o` or `-o=`) is insufficient when CLI utilities (like `ilspycmd`) accept options bundled without spaces (`-otmp/path`).
**Prevention:** Explicitly separate logic for long (`--`) and short (`-`/`/`) options. For short options, extract the literal character prefix and validate it defensively. Use a safe prefix allowlist to maintain functionality for similarly named options (e.g. `-ds` and `-d`).
