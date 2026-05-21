## 2025-05-21 - Command Injection via ilspycmd Execution
**Vulnerability:** Command/Argument Injection in `ilspy-worker/Program.cs`. Untrusted user inputs (`type_name`, `assembly_path`) were concatenated into a single string to execute `ilspycmd`.
**Learning:** `ProcessStartInfo(filename, arguments_string)` executes a process where arguments are evaluated as a single string. If quotes and spaces are not properly escaped, malicious input can inject arbitrary flags (e.g., `-o` for file writes).
**Prevention:** Always use `ProcessStartInfo`'s `ArgumentList.Add()` or pass an array of discrete arguments. Avoid formatting command strings manually when dealing with user inputs.
