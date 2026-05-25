using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

var ilspycmd = Environment.GetEnvironmentVariable("RBM_ILSPY_CMD");
if (string.IsNullOrWhiteSpace(ilspycmd))
    ilspycmd = "ilspycmd";
var sessions = new ConcurrentDictionary<string, CachedAssembly>();

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;

    JsonDocument? request;
    try { request = JsonDocument.Parse(line); }
    catch { WriteError(null, "invalid JSON"); continue; }

    var root = request.RootElement;
    var id = root.TryGetProperty("id", out var idEl) ? idEl : default;
    var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
    var prms = root.TryGetProperty("params", out var p) ? p : default;

    try
    {
        object result = method switch
        {
            "ping" => new { status = "ok" },
            "open" => OpenAssembly(prms),
            "close" => CloseAssembly(prms),
            "sessions" => new { sessions_list = sessions.Keys.ToList() },
            "types" => ListTypes(prms),
            "namespaces" => ListNamespaces(prms),
            "decompile" => DecompileType(prms),
            "il" => GetIL(prms),
            "metadata" => GetMetadata(prms),
            "resources" => ListResources(prms),
            "typeinfo" => TypeInfo(prms),
            "members" => ListMembers(prms),
            "references" => ListReferences(prms),
            "search" => SearchTypes(prms),
            "method_source" => GetMethodSource(prms),
            "source_search" => SearchSource(prms),
            "raw_cmd" => RunRawCmd(prms),
            "find_usages" => FindUsages(prms),
            _ => throw new ArgumentException($"unknown method: {method}"),
        };
        WriteResult(id, result);
    }
    catch (Exception ex)
    {
        WriteError(id, ex.Message);
    }
}

string IlspyCmd(params string[] args)
{
    var timeoutMs = 60000; // 60s default
    var envTimeout = Environment.GetEnvironmentVariable("RBM_ILSPY_CMD_TIMEOUT_MS");
    if (!string.IsNullOrEmpty(envTimeout) && int.TryParse(envTimeout, out var parsed) && parsed > 0)
        timeoutMs = parsed;

    var psi = new ProcessStartInfo(ilspycmd)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var arg in args)
    {
        psi.ArgumentList.Add(arg);
    }
    using var proc = Process.Start(psi) ?? throw new Exception("failed to start ilspycmd");
    var completed = proc.WaitForExit(timeoutMs);
    if (!completed)
    {
        try { proc.Kill(); } catch { }
        var argsStr = string.Join(" ", args);
        throw new Exception($"ilspycmd timed out after {timeoutMs}ms: {argsStr[..Math.Min(argsStr.Length, 100)]}");
    }
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (proc.ExitCode != 0)
        throw new Exception(string.IsNullOrEmpty(stderr.Trim()) ? stdout.Trim() : stderr.Trim());
    return stdout;
}


List<MemberInfo> ParseMemberSignatures(string code)
{
    var members = new List<MemberInfo>();
    var lines = code.Split('\n');

    for (var i = 0; i < lines.Length; i++)
    {
        var raw = lines[i];
        var line = raw.Trim();
        if (string.IsNullOrEmpty(line)) continue;

        // Skip type declarations, attributes, comments, usings, namespaces, braces
        if (line.StartsWith("using ") || line.StartsWith("namespace ") ||
            line.StartsWith("[") || line.StartsWith("//") || line.StartsWith("/*") ||
            line.StartsWith("}") || line.StartsWith("#") || line == "{" || line == "}")
            continue;

        // Skip type/interface/struct/enum/delegate declarations
        var trimmedForType = line;
        foreach (var prefix in ParseHelpers.AccessModifiers)
            if (trimmedForType.StartsWith(prefix)) { trimmedForType = trimmedForType[prefix.Length..]; break; }
        foreach (var prefix in ParseHelpers.TypeModifiers)
            if (trimmedForType.StartsWith(prefix)) { trimmedForType = trimmedForType[prefix.Length..]; break; }
        if (trimmedForType.StartsWith("class ") || trimmedForType.StartsWith("struct ") ||
            trimmedForType.StartsWith("interface ") || trimmedForType.StartsWith("enum ") ||
            trimmedForType.StartsWith("delegate "))
            continue;

        // Match lines starting with access modifiers or member keywords
        bool isMember = false;
        foreach (var prefix in ParseHelpers.MemberModifiers)
        {
            if (line.StartsWith(prefix)) { isMember = true; break; }
        }
        if (!isMember) continue;

        // Extract signature before { or ; or =
        var sigEnd = line.IndexOfAny(['{', ';', '=']);
        var sig = sigEnd >= 0 ? line[..sigEnd].Trim() : line;

        // Look ahead for multi-line property patterns
        bool looksLikeProperty = false;
        if (!line.Contains('('))
        {
            // Check current line for compact auto-props: { get; set; }
            if (line.Contains(" get;") || line.Contains(" set;"))
                looksLikeProperty = true;
            else if (line.Contains('{') || (i + 1 < lines.Length && lines[i + 1].Trim() == "{"))
            {
                // Scan next lines for get/set — stop at empty line or }
                for (var j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
                {
                    var next = lines[j].Trim();
                    if (next == "") break;
                    if (next == "}") break;
                    if (next.Contains("get") || next.Contains("set"))
                    {
                        looksLikeProperty = true;
                        break;
                    }
                }
            }
        }

        // Determine kind
        var kind = "method";

        // Expression-bodied properties: Type Name => value;
        // Check if '=>' appears before any '(' — if so, it's a property
        var arrowIdx = line.IndexOf("=>");
        var parenIdx = line.IndexOf('(');
        if (arrowIdx >= 0 && (parenIdx < 0 || arrowIdx < parenIdx))
            looksLikeProperty = true;

        if (looksLikeProperty || line.Contains(" get;") || line.Contains(" set;") ||
            line.Contains(" { get;") || line.Contains(" { set;") ||
            line.Contains("get =>") || (sig.Contains(" get") && sig.Contains(" set")))
            kind = "property";
        else if (line.Contains(" event "))
            kind = "event";
        else if (!line.Contains("("))
            kind = "field";

        members.Add(new MemberInfo { signature = sig, kind = kind });
    }

    return members;
}

static TypeInfo ParseTypeName(string raw)
{
    // raw format: "Kind Namespace.TypeName" e.g. "Class System.Net.Http.HttpClient"
    var kind = "class";
    var name = raw;

    var spaceIdx = raw.IndexOf(' ');
    if (spaceIdx > 0)
    {
        var prefix = raw[..spaceIdx];
        name = raw[(spaceIdx + 1)..];
        kind = prefix.ToLowerInvariant() switch
        {
            "class" => "class",
            "interface" => "interface",
            "struct" => "struct",
            "enum" => "enum",
            "delegate" => "delegate",
            _ => "class",
        };
    }

    var dotIdx = name.LastIndexOf('.');
    string ns = "";
    string simpleName = name;
    if (dotIdx > 0)
    {
        ns = name[..dotIdx];
        simpleName = name[(dotIdx + 1)..];
    }

    return new TypeInfo { Kind = kind, Namespace = ns, Name = simpleName, FullName = name };
}

CachedAssembly LoadAssembly(string path, string sid)
{
    var output = IlspyCmd("-l", "class", path);
    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();

    if (lines.Count == 0 || lines.All(e => e.StartsWith("Specify") || e.StartsWith("Usage")))
        throw new Exception($"ilspycmd returned no valid types for: {path}");

    var types = lines.Select(ParseTypeName).ToList();
    var nsGroups = types.GroupBy(t => t.Namespace)
        .OrderByDescending(g => g.Count())
        .Select(g => new NamespaceGroup(g.Key, g.Count()))
        .ToList();

    var cached = new CachedAssembly
    {
        Path = path,
        Types = types,
        NamespaceGroups = nsGroups,
    };
    sessions[sid] = cached;
    return cached;
}

CachedAssembly GetOrOpen(JsonElement prms)
{
    var sid = GetString(prms, "session_id");
    if (sid != null && sessions.TryGetValue(sid, out var cached))
        return cached;
    var path = GetString(prms, "assembly_path") ?? throw new ArgumentException("session_id not found. open the assembly first or provide assembly_path");
    sid = sid ?? Path.GetFileName(path);
    return LoadAssembly(path, sid);
}

object OpenAssembly(JsonElement prms)
{
    var path = GetString(prms, "assembly_path") ?? throw new ArgumentException("assembly_path required");
    var sid = GetString(prms, "session_id") ?? Path.GetFileName(path);

    if (sessions.ContainsKey(sid))
    {
        var cached = sessions[sid];
        return new
        {
            session_id = sid,
            status = "already_open",
            types_count = cached.Types.Count,
            namespaces_count = cached.NamespaceGroups.Count,
            top_namespaces = cached.NamespaceGroups.Take(5).Select(n => new { ns = n.ns, types = n.count }),
        };
    }

    var loaded = LoadAssembly(path, sid);
    return new
    {
        session_id = sid,
        status = "opened",
        types_count = loaded.Types.Count,
        namespaces_count = loaded.NamespaceGroups.Count,
        top_namespaces = loaded.NamespaceGroups.Take(5).Select(n => new { ns = n.ns, types = n.count }),
        types_preview = loaded.Types.Take(3).Select(t => new { name = t.FullName, kind = t.Kind }).ToList(),
    };
}

object CloseAssembly(JsonElement prms)
{
    var sid = GetString(prms, "session_id") ?? throw new ArgumentException("session_id required");
    return new { session_id = sid, closed = sessions.TryRemove(sid, out _) };
}

object ListNamespaces(JsonElement prms)
{
    var cached = GetOrOpen(prms);
    var filter = GetString(prms, "filter") ?? "";
    var offset = GetInt(prms, "offset") ?? 0;
    var limit = GetInt(prms, "limit") ?? 0;

    var filtered = cached.NamespaceGroups.AsEnumerable();
    if (!string.IsNullOrEmpty(filter))
        filtered = filtered.Where(n => n.ns.Contains(filter, StringComparison.OrdinalIgnoreCase));

    var total = filtered.Count();
    if (limit > 0) filtered = filtered.Skip(offset).Take(limit);

    return new
    {
        total_matched = total,
        returned = filtered.Count(),
        namespaces = filtered.Select(n => new { @namespace = n.ns, type_count = n.count }).ToList(),
    };
}

object ListTypes(JsonElement prms)
{
    var cached = GetOrOpen(prms);
    var filter = GetString(prms, "filter") ?? "";
    var offset = GetInt(prms, "offset") ?? 0;
    var limit = GetInt(prms, "limit") ?? 0;
    var ns = GetString(prms, "namespace") ?? "";

    IEnumerable<TypeInfo> filtered = cached.Types;
    if (!string.IsNullOrEmpty(filter))
        filtered = filtered.Where(t => t.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrEmpty(ns))
        filtered = filtered.Where(t => t.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase));

    var total = filtered.Count();
    if (limit > 0) filtered = filtered.Skip(offset).Take(limit);

    return new
    {
        total_matched = total,
        returned = filtered.Count(),
        types = filtered.Select(t => new { name = t.FullName, kind = t.Kind, ns = t.Namespace }).ToList(),
    };
}

object DecompileType(JsonElement prms)
{
    var typeName = GetString(prms, "type_name") ?? throw new ArgumentException("type_name required");
    var ap = ResolvePath(prms);

    // Check decompile cache
    var sid = GetString(prms, "session_id");
    if (sid != null && sessions.TryGetValue(sid, out var cached))
    {
        if (cached.Decompiled.TryGetValue(typeName, out var cachedCode))
            return new { type_name = typeName, code = cachedCode, cached = true };
    }

    var output = IlspyCmd("-t", typeName, ap);

    // Cache result
    if (sid != null && sessions.TryGetValue(sid, out var cacheTarget))
        cacheTarget.Decompiled[typeName] = output;

    return new { type_name = typeName, code = output, cached = false };
}

object GetIL(JsonElement prms)
{
    var typeName = GetString(prms, "type_name") ?? throw new ArgumentException("type_name required");
    var ap = ResolvePath(prms);
    var output = IlspyCmd("-il", "-t", typeName, ap);
    return new { type_name = typeName, il = output };
}

object GetMetadata(JsonElement prms)
{
    var cached = GetOrOpen(prms);
    return new
    {
        assembly_path = cached.Path,
        types_count = cached.Types.Count,
        namespaces_count = cached.NamespaceGroups.Count,
    };
}

object ListResources(JsonElement prms)
{
    var ap = ResolvePath(prms);
    var output = IlspyCmd("-l", "resource", ap);
    var resources = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => !string.IsNullOrEmpty(t)).ToList();
    return new { resource_count = resources.Count, resources };
}

object ListMembers(JsonElement prms)
{
    var typeName = GetString(prms, "type_name") ?? throw new ArgumentException("type_name required");
    var ap = ResolvePath(prms);

    // Check member cache
    var sid = GetString(prms, "session_id");
    if (sid != null && sessions.TryGetValue(sid, out var cached))
    {
        if (cached.Members.TryGetValue(typeName, out var cachedMembers))
            return new { type_name = typeName, member_count = cachedMembers.Count, members = cachedMembers, cached = true };
    }

    var output = IlspyCmd("-t", typeName, ap);
    var members = ParseMemberSignatures(output);

    // Cache result
    if (sid != null && sessions.TryGetValue(sid, out var cacheTarget))
        cacheTarget.Members[typeName] = members;

    return new { type_name = typeName, member_count = members.Count, members, cached = false };
}

object ListReferences(JsonElement prms)
{
    var ap = ResolvePath(prms);
    // ilspycmd --project outputs a .csproj with reference info, but it's huge.
    // Instead, decompile a small system type and extract using directives,
    // or just show the assembly names from the session.
    var sid = GetString(prms, "session_id");
    if (sid != null && sessions.TryGetValue(sid, out var cached))
    {
        var refs = new System.Collections.Generic.HashSet<string>();
        foreach (var t in cached.Types.Take(50))
        {
            var ns = t.Namespace;
            if (!string.IsNullOrEmpty(ns) && !ns.StartsWith("<") && !ns.StartsWith("Newtonsoft.Json"))
            {
                var top = ns.Split('.').FirstOrDefault();
                if (top != null) refs.Add(top);
            }
        }
        return new { reference_count = refs.Count, references = refs.OrderBy(r => r).ToList() };
    }
    return new { reference_count = 0, references = new List<string>() };
}

object SearchTypes(JsonElement prms)
{
    var cached = GetOrOpen(prms);
    var pattern = GetString(prms, "pattern") ?? "";
    var entity = GetString(prms, "entity") ?? "type";
    var limit = GetInt(prms, "limit") ?? 20;

    if (entity == "member")
    {
        // Search across all cached member name lists
        var results = new List<object>();
        foreach (var kv in cached.Members)
        {
            var typeName = kv.Key;
            var matchingMembers = kv.Value
                .Where(m => m.signature.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .Select(m => new { type = typeName, signature = m.signature, kind = m.kind })
                .ToList();
            results.AddRange(matchingMembers);
            if (results.Count >= limit) break;
        }
        return new { pattern, entity = "member", total = results.Count, results = results.Take(limit).ToList() };
    }

    // Default: search type names
    var matches = cached.Types
        .Where(t => t.FullName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
        .Take(limit)
        .Select(t => new { name = t.FullName, kind = t.Kind, ns = t.Namespace })
        .ToList();
    return new { pattern, entity = "type", total = matches.Count, results = matches };
}

object TypeInfo(JsonElement prms)
{
    var typeName = GetString(prms, "type_name") ?? throw new ArgumentException("type_name required");
    var ap = ResolvePath(prms);

    // Decompile the type and extract type-level metadata
    var output = IlspyCmd("-t", typeName, ap);
    var lines = output.Split('\n');

    // Find the class/struct/interface declaration line
    string? declaration = null;
    string? inherits = null;
    var attributes = new List<string>();

    foreach (var raw in lines)
    {
        var line = raw.Trim();

        // Collect attributes [AttributeName]
        if (line.StartsWith("[") && line.EndsWith("]"))
        {
            attributes.Add(line.Trim('[', ']'));
            continue;
        }

        // Find declaration like: public sealed class Foo : Bar, IBaz
        if ((line.Contains(" class ") || line.Contains(" struct ") ||
             line.Contains(" interface ") || line.Contains(" enum ") ||
             line.Contains(" record ")) &&
            (line.StartsWith("public") || line.StartsWith("internal") ||
             line.StartsWith("sealed") || line.StartsWith("abstract") ||
             line.StartsWith("static") || line.StartsWith("readonly")))
        {
            declaration = line;

            // Extract inheritance after ':'
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                inherits = line[(colonIdx + 1)..].Trim();
                // Remove trailing '{' or whitespace
                var braceIdx = inherits.IndexOf('{');
                if (braceIdx >= 0)
                    inherits = inherits[..braceIdx].Trim();
            }
            break;
        }
    }

    // Parse modifiers
    var modifiers = new List<string>();
    if (declaration != null)
    {
        var kind = "class";
        if (declaration.Contains(" struct ")) kind = "struct";
        else if (declaration.Contains(" interface ")) kind = "interface";
        else if (declaration.Contains(" enum ")) kind = "enum";
        else if (declaration.Contains(" record ")) kind = "record";

        if (declaration.Contains(" sealed ")) modifiers.Add("sealed");
        if (declaration.Contains(" abstract ")) modifiers.Add("abstract");
        if (declaration.Contains(" static ")) modifiers.Add("static");
        if (declaration.Contains(" readonly ")) modifiers.Add("readonly");

        return new
        {
            type_name = typeName,
            kind,
            modifiers,
            attributes,
            inherits_from = inherits ?? "",
        };
    }

    return new { type_name = typeName, error = "could not parse type declaration" };
}

object GetMethodSource(JsonElement prms)
{
    var typeName = GetString(prms, "type_name") ?? throw new ArgumentException("type_name required");
    var methodName = GetString(prms, "method_name") ?? throw new ArgumentException("method_name required");
    var ap = ResolvePath(prms);

    // Check decompile cache first
    var sid = GetString(prms, "session_id");
    string code;
    if (sid != null && sessions.TryGetValue(sid, out var cached) && cached.Decompiled.TryGetValue(typeName, out var cachedCode))
        code = cachedCode;
    else
    {
        code = IlspyCmd("-t", typeName, ap);
        if (sid != null && sessions.TryGetValue(sid, out var c))
            c.Decompiled[typeName] = code;
    }

    // Find the method in the decompiled code
    var lines = code.Split('\n');
    var methodLines = new List<string>();
    var inMethod = false;
    var braceDepth = 0;
    var seenOpeningBrace = false;

    foreach (var raw in lines)
    {
        var line = raw.TrimEnd();

        if (!inMethod)
        {
            // Look for method declaration with the matching name
            if (line.Contains($" {methodName}(") || line.Contains($" {methodName}<") ||
                line.Contains($" {methodName} ") || line.EndsWith($" {methodName}"))
            {
                inMethod = true;
                braceDepth = 0;
                seenOpeningBrace = false;
                methodLines.Add(raw);
                continue;
            }
        }
        else
        {
            methodLines.Add(raw);
            // Only track brace depth after we've seen the opening '{'
            if (!seenOpeningBrace)
            {
                if (line.Contains('{'))
                {
                    seenOpeningBrace = true;
                    braceDepth = line.Count(c => c == '{') - line.Count(c => c == '}');
                }
            }
            else
            {
                braceDepth += line.Count(c => c == '{');
                braceDepth -= line.Count(c => c == '}');
                if (braceDepth <= 0) break;
            }
        }
    }

    var source = string.Join("\n", methodLines);
    return new { type_name = typeName, method_name = methodName, source, line_count = methodLines.Count };
}

object SearchSource(JsonElement prms)
{
    var pattern = GetString(prms, "pattern") ?? throw new ArgumentException("pattern required");
    var typeName = GetString(prms, "type_name") ?? throw new ArgumentException("type_name required");
    var ap = ResolvePath(prms);

    // Decompile (or use cache)
    var sid = GetString(prms, "session_id");
    string code;
    if (sid != null && sessions.TryGetValue(sid, out var cached) && cached.Decompiled.TryGetValue(typeName, out var cachedCode))
        code = cachedCode;
    else
    {
        code = IlspyCmd("-t", typeName, ap);
        if (sid != null && sessions.TryGetValue(sid, out var c))
            c.Decompiled[typeName] = code;
    }

    var matches = new List<object>();
    var lineNum = 1;
    foreach (var lineSpan in code.AsSpan().EnumerateLines())
    {
        if (lineSpan.Contains(pattern, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(new { line = lineNum, text = lineSpan.Trim().ToString() });
        }
        lineNum++;
    }

    return new { type_name = typeName, pattern, total_matches = matches.Count, matches = matches.Take(50).ToList() };
}

object RunRawCmd(JsonElement prms)
{
    var args = GetString(prms, "args") ?? throw new ArgumentException("args required");
    var ap = ResolvePath(prms);

    var parsedArgs = new List<string>();
    var currentArg = new System.Text.StringBuilder();
    bool inQuotes = false;

    for (int i = 0; i < args.Length; i++)
    {
        char c = args[i];

        if (c == '"')
        {
            inQuotes = !inQuotes;
        }
        else if (char.IsWhiteSpace(c) && !inQuotes)
        {
            if (currentArg.Length > 0)
            {
                parsedArgs.Add(currentArg.ToString());
                currentArg.Clear();
            }
        }
        else
        {
            currentArg.Append(c);
        }
    }

    if (currentArg.Length > 0)
    {
        parsedArgs.Add(currentArg.ToString());
    }

    // Security: Prevent arbitrary file write/overwrite and unintended tool execution
    // Normalize arguments by stripping leading - or / to catch variants like /o, --o, etc.
    var dangerousOptions = new[] { "o", "outputdir", "p", "project", "d", "dump-package", "genpdb", "generate-pdb", "generate-diagrammer" };
    foreach (var arg in parsedArgs)
    {
        if (arg.StartsWith('-') || arg.StartsWith('/'))
        {
            var normalizedArg = arg.TrimStart('-', '/');
            foreach (var dangerous in dangerousOptions)
            {
                if (normalizedArg.Equals(dangerous, StringComparison.OrdinalIgnoreCase) ||
                    normalizedArg.StartsWith(dangerous + "=", StringComparison.OrdinalIgnoreCase) ||
                    normalizedArg.StartsWith(dangerous + ":", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"dangerous or unsupported argument: {arg}");
                }
            }
        }
    }

    parsedArgs.Add(ap);

    var output = IlspyCmd(parsedArgs.ToArray());
    return new { command = $"{args} {ap}", output };
}

object FindUsages(JsonElement prms)
{
    var pattern = GetString(prms, "pattern") ?? throw new ArgumentException("pattern required");
    var ap = ResolvePath(prms);
    var sid = GetString(prms, "session_id");

    if (sid == null || !sessions.TryGetValue(sid, out var cached))
        throw new ArgumentException("session not found");

    var results = new List<object>();
    var searchedTypes = 0;

    foreach (var type in cached.Types)
    {
        if (searchedTypes >= 50) break; // cap search

        string code;
        if (cached.Decompiled.TryGetValue(type.FullName, out var cachedCode))
            code = cachedCode;
        else
            continue; // skip types we haven't decompiled yet

        searchedTypes++;
        var matches = new List<object>();
        var lineNum = 1;
        foreach (var lineSpan in code.AsSpan().EnumerateLines())
        {
            if (lineSpan.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new { line = lineNum, text = lineSpan.Trim().ToString() });
            }
            lineNum++;
        }

        if (matches.Count > 0)
            results.Add(new { type = type.FullName, total_matches = matches.Count, matches = matches.Take(20).ToList() });
    }

    return new { pattern, types_searched = searchedTypes, types_with_matches = results.Count, results };
}

string ResolvePath(JsonElement prms)
{
    var sid = GetString(prms, "session_id");
    if (sid != null && sessions.TryGetValue(sid, out var cached))
        return cached.Path;
    return GetString(prms, "assembly_path") ?? throw new ArgumentException("session_id not found. open the assembly first or provide assembly_path");
}

void WriteResult(JsonElement id, object result)
{
    Console.WriteLine(JsonSerializer.Serialize(new { id = GetIdValue(id), result }));
}

void WriteError(JsonElement? id, string message)
{
    Console.WriteLine(JsonSerializer.Serialize(new { id = id is JsonElement i ? GetIdValue(i) : null, error = message }));
}

object? GetIdValue(JsonElement id) => id.ValueKind switch
{
    JsonValueKind.Number => id.GetInt32(),
    JsonValueKind.String => id.GetString(),
    _ => null
};

string? GetString(JsonElement obj, string key) =>
    obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

int? GetInt(JsonElement obj, string key) =>
    obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;

record MemberInfo
{
    public string signature { get; set; } = "";
    public string kind { get; set; } = "method";
}

record TypeInfo
{
    public string Kind { get; set; } = "class";
    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
}

class CachedAssembly
{
    public string Path { get; set; } = "";
    public List<TypeInfo> Types { get; set; } = [];
    public List<NamespaceGroup> NamespaceGroups { get; set; } = [];
    public Dictionary<string, string> Decompiled { get; set; } = new();
    public Dictionary<string, List<MemberInfo>> Members { get; set; } = new();
}

record NamespaceGroup(string ns, int count);

public static class ParseHelpers
{
    public static readonly string[] AccessModifiers = { "public ", "private ", "internal ", "protected ", "protected internal ", "private protected " };
    public static readonly string[] TypeModifiers = { "static ", "abstract ", "sealed ", "readonly ", "unsafe ", "partial " };
    public static readonly string[] MemberModifiers = { "public ", "private ", "internal ", "protected ",
        "protected internal ", "private protected ",
        "static ", "virtual ", "override ", "abstract ", "sealed ",
        "readonly ", "const ", "extern ", "new ", "unsafe ", "async " };
}

