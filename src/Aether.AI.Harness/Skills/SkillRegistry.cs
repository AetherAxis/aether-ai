using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Harness.Skills;

/// <summary>skill 注册贡献者接口——每个适配器通过此接口贡献 skill（提示词级能力）。</summary>
public interface ISkillRegistryContributor
{
    void Contribute(SkillRegistry registry);
}

public sealed class DelegateSkillRegistryContributor : ISkillRegistryContributor
{
    private readonly Action<SkillRegistry> _configure;
    public DelegateSkillRegistryContributor(Action<SkillRegistry> configure) => _configure = configure;
    public void Contribute(SkillRegistry registry) => _configure(registry);
}

/// <summary>skill 元数据 + 指令正文（纯数据，无 MAF 依赖）。</summary>
public sealed class SkillDefinition
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    /// <summary>skill 指令正文（Markdown），即 agent 加载该 skill 后注入的提示。</summary>
    public string Content { get; }

    public SkillDefinition(string name, string description, string category, string content)
    {
        Name = name;
        Description = description;
        Category = category;
        Content = content;
    }
}

/// <summary>skill 元数据（名称 + 描述 + 分类），供 UI 选择展示。</summary>
public sealed class SkillMeta
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }

    public SkillMeta(string name, string description, string category)
    {
        Name = name;
        Description = description;
        Category = category;
    }
}

/// <summary>
/// Registry of prompt-based agent skills, keyed by skill name. Skills are static
/// instruction text（无工厂、无需 DI 解析），由各 AddXxx 模块通过
/// <see cref="ISkillRegistryContributor"/> 在启动期注册。HarnessAgent 启用 skills 时，
/// AgentFactory 取出全部（或子集）skill 构造 <c>AgentInMemorySkillsSource</c>。
/// </summary>
public sealed class SkillRegistry
{
    /// <summary>内容变更通知：skill 注册/重扫盘后触发（<see cref="Register"/> / <see cref="ReloadFromDirectory"/> 完成时）。
    /// 让专家页技能 tab、agent 工具 skill_add 写入后订阅方自动刷新，无需手动切 tab/点刷新。</summary>
    public event EventHandler? Changed;

    private readonly ConcurrentDictionary<string, SkillDefinition> _entries = new();

    public SkillRegistry() { }

    public SkillRegistry(IServiceProvider serviceProvider)
    {
        // 执行各 AddXxx 模块通过 ConfigureSkillRegistry 注册的 skill
        foreach (var contributor in serviceProvider.GetServices<ISkillRegistryContributor>())
        {
            try { contributor.Contribute(this); }
            catch { /* skip */ }
        }
    }

    /// <summary>注册一个 skill（name/description/category + 指令正文）。</summary>
    public SkillRegistry Register(string name, string description, string category, string content)
    {
        _entries[name] = new SkillDefinition(name, description, category, content);
        return this;
    }

    /// <summary>获取所有 skill 的元数据（不包含正文），供 UI 展示。</summary>
    public IReadOnlyList<SkillMeta> GetAllMetas()
        => _entries.Values
            .Select(s => new SkillMeta(s.Name, s.Description, s.Category))
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .ToList();

    /// <summary>取全部 skill 定义（含正文）。AgentFactory 在启用 skills 时调用。</summary>
    public IReadOnlyList<SkillDefinition> GetAll()
        => _entries.Values
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .ToList();

    /// <summary>按名解析 skill 子集（第二期 per-agent 选择用；找不到的静默跳过）。</summary>
    public IReadOnlyList<SkillDefinition> ResolveAll(IEnumerable<string> names)
    {
        var resolved = new List<SkillDefinition>();
        foreach (var name in names)
        {
            if (_entries.TryGetValue(name, out var def))
                resolved.Add(def);
        }
        return resolved;
    }

    public bool Contains(string name) => _entries.ContainsKey(name);
    public IReadOnlyCollection<string> AllNames => _entries.Keys.ToList();

    /// <summary>
    /// 从目录加载 SKILL.md 文件。约定：每个 skill 占一个子目录
    /// <c>&lt;directory&gt;/&lt;skill-name&gt;/SKILL.md</c>（与 MAF/Claude 的 skill 目录布局一致）。
    /// SKILL.md 为 YAML frontmatter（name/description/可选 category）+ Markdown 正文。
    /// 目录不存在或子目录无 SKILL.md 时静默跳过，不抛异常。
    /// </summary>
    public SkillRegistry LoadFromDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return this;

        foreach (var skillDir in Directory.GetDirectories(directory))
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            try { LoadSkillFile(skillDir, skillFile); }
            catch { /* 跳过格式错误的 skill 文件 */ }
        }
        return this;
    }

    /// <summary>
    /// 清空已加载 skill 并从目录重扫盘——供 UI 刷新反映磁盘实时态（新增/编辑/删除 SKILL.md 后无需重启），
    /// 与运行时 <c>AgentFileSkillsSource</c> 直读目录的行为对齐。
    /// </summary>
    /// <remarks>会清空所有已注册 skill（含程序式 <see cref="Register"/> 的）。当前桌面/移动均仅以
    /// <see cref="LoadFromDirectory"/> 为唯一来源（无 <see cref="ISkillRegistryContributor"/> 程序式贡献），故 Clear 安全；
    /// 若将来加程序式 skill 贡献者，需在此重跑 contributor 或经注入 <c>IServiceProvider</c> 的入口重载。</remarks>
    public SkillRegistry ReloadFromDirectory(string directory)
    {
        _entries.Clear();
        var result = LoadFromDirectory(directory);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void LoadSkillFile(string skillDir, string skillFile)
    {
        var text = File.ReadAllText(skillFile);
        var (front, body) = SplitFrontmatter(text);
        var name = GetFrontmatterValue(front, "name") ?? Path.GetFileName(skillDir);
        var description = GetFrontmatterValue(front, "description") ?? "";
        var category = GetFrontmatterValue(front, "category") ?? "通用";
        Register(name, description, category, body);
    }

    private static (string front, string body) SplitFrontmatter(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith("---", StringComparison.Ordinal)) return ("", text.Trim());
        var rest = t.Substring(3);
        var idx = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (idx < 0) return ("", text.Trim());
        var front = rest.Substring(0, idx);
        var body = rest.Substring(idx + 4).TrimStart('\r', '\n');
        return (front, body);
    }

    private static string? GetFrontmatterValue(string front, string key)
    {
        foreach (var line in front.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ci = line.IndexOf(':');
            if (ci < 0) continue;
            if (line.Substring(0, ci).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line.Substring(ci + 1).Trim().Trim('\'', '"');
        }
        return null;
    }
}
