using System.Text.Json;

namespace Aether.AI.Abstractions;

/// <summary>
/// 一个 MCP server 的连接配置（纯 POCO，放 Abstractions 便于 VM/接口/business 共用，不引 alpha）。
/// 支持 stdio（本地子进程）或 HTTP/SSE（远程）两种 transport。格式与主流 <c>.mcp.json</c> 对齐：
/// <c>command</c>+<c>args</c>(<c>arguments</c>)+<c>env</c>（stdio）或 <c>url</c>+<c>headers</c>（HTTP）。
/// </summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = string.Empty;
    public string? Command { get; set; }
    public IReadOnlyList<string>? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public IReadOnlyDictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);
    public bool IsSse => !string.IsNullOrWhiteSpace(Url);

    /// <summary>展示用的 transport 摘要：stdio: &lt;command&gt; / http: &lt;url&gt; / 未配置。</summary>
    public string TransportDisplay =>
        IsStdio ? $"stdio: {Command}" :
        IsSse ? $"http: {Url}" :
        "未配置";

    /// <summary>
    /// 把一段 JSON 解析为单个 <see cref="McpServerConfig"/>。兼容：单个对象、
    /// <c>{ "mcpServers": { "name": {...} } }</c> 映射（取第一个）、数组（取第一个）。解析失败返回 null。
    /// 供编辑表单（单 JSON 文本框）使用。
    /// </summary>
    public static McpServerConfig? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 数组：取第一个对象
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) return ParseElement(item, null);
                return null;
            }

            // mcpServers 映射：key 即 name，取第一个 server
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("mcpServers", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in map.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        return ParseElement(prop.Value, prop.Name); // key 就是 name
                return null;
            }

            // 单个 server 对象：name 从内部 name 字段读
            if (root.ValueKind == JsonValueKind.Object)
                return ParseElement(root, null);

            return null;
        }
        catch { return null; }
    }

    /// <summary>序列化为 mcpServers 映射格式（与 Trae/Claude Code/Cursor 一致）的 pretty JSON。</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(Name);
            WriteBody(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private void WriteBody(Utf8JsonWriter w)
    {
        if (!string.IsNullOrEmpty(Command))
        {
            w.WriteString("command", Command);
            w.WritePropertyName("args");
            w.WriteStartArray();
            if (Arguments is not null) foreach (var a in Arguments) w.WriteStringValue(a);
            w.WriteEndArray();
            if (!string.IsNullOrEmpty(WorkingDirectory)) w.WriteString("cwd", WorkingDirectory);
            if (Env is not null && Env.Count > 0)
            {
                w.WriteStartObject("env");
                foreach (var kv in Env) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
            }
        }
        else if (!string.IsNullOrEmpty(Url))
        {
            w.WriteString("url", Url);
            if (Headers is not null && Headers.Count > 0)
            {
                w.WriteStartObject("headers");
                foreach (var kv in Headers) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
            }
        }
    }

    public static McpServerConfig ParseElement(JsonElement el, string? name)
    {
        string? Get(string n) =>
            el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        IReadOnlyList<string>? args = null;
        if (el.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
            args = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList();
        else if (el.TryGetProperty("arguments", out a) && a.ValueKind == JsonValueKind.Array)
            args = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList();

        return new McpServerConfig
        {
            Name = name ?? Get("name") ?? string.Empty,
            Command = Get("command"),
            Arguments = args,
            WorkingDirectory = Get("workingDirectory") ?? Get("cwd"),
            Env = ParseStringMap(el, "env"),
            Url = Get("url"),
            Headers = ParseStringMap(el, "headers"),
        };
    }

    private static IReadOnlyDictionary<string, string>? ParseStringMap(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in p.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString() ?? "";
        return dict.Count == 0 ? null : dict;
    }
}
