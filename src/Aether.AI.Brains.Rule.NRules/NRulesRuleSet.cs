using System.Reflection;
using System.Text.Json;
using NRulesRule = global::NRules.Fluent.Dsl.Rule;

namespace Aether.AI.Brains.Rule.NRules;

/// <summary>
/// 解析 <c>RuleSource</c> 的 NRules 规则集声明。NRules 的规则是编译后的 C# 类（<c>Rule</c> 子类），
/// 字符串承载不了规则体，因此这里只声明「加载哪些规则」：
/// <code>
/// { "ruleTypes": ["MyApp.Rules.QueryRule"] }
/// { "ruleAssemblies": ["MyApp.Rules"] }
/// </code>
/// 也接受以换行/分号分隔的纯类型名列表。<c>ruleTypes</c> 可用简单名（在当前已加载程序集里查找）
/// 或程序集限定名（<c>Ns.Type, Assembly</c>）。
/// </summary>
internal static class NRulesRuleSet
{
    public static (IReadOnlyList<Type> Types, IReadOnlyList<Assembly> Assemblies) Resolve(string ruleSource)
    {
        var (typeNames, assemblyNames) = Parse(ruleSource);
        if (typeNames.Count == 0 && assemblyNames.Count == 0)
        {
            throw new InvalidOperationException(
                "NRules RuleSource 未声明规则：需要 ruleTypes 或 ruleAssemblies。");
        }

        var types = typeNames.Select(ResolveType).ToArray();
        var assemblies = assemblyNames.Select(ResolveAssembly).ToArray();
        return (types, assemblies);
    }

    private static (List<string> Types, List<string> Assemblies) Parse(string source)
    {
        var types = new List<string>();
        var assemblies = new List<string>();
        var trimmed = source.Trim();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    CollectStrings(root, types);
                }
                else
                {
                    if (root.TryGetProperty("ruleTypes", out var typeArray))
                    {
                        CollectStrings(typeArray, types);
                    }
                    if (root.TryGetProperty("ruleAssemblies", out var assemblyArray))
                    {
                        CollectStrings(assemblyArray, assemblies);
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"NRules RuleSource 不是合法 JSON：{ex.Message}", ex);
            }

            return (types, assemblies);
        }

        // 纯文本：按换行/分号分隔（不用逗号——程序集限定类型名本身含逗号）。
        foreach (var entry in trimmed.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            types.Add(entry);
        }

        return (types, assemblies);
    }

    private static void CollectStrings(JsonElement element, List<string> target)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("NRules RuleSource 的 ruleTypes / ruleAssemblies 必须是字符串数组。");
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidOperationException("NRules RuleSource 的 ruleTypes / ruleAssemblies 必须是字符串数组。");
            }
            target.Add(item.GetString()!);
        }
    }

    private static Type ResolveType(string name)
    {
        var type = Type.GetType(name, throwOnError: false);
        if (type is null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(name, throwOnError: false);
                if (type is not null)
                {
                    break;
                }
            }
        }

        if (type is null)
        {
            throw new InvalidOperationException(
                $"NRules 找不到规则类型 '{name}'。请使用程序集限定名（'Ns.Type, Assembly'），或先确保该程序集已加载。");
        }

        if (!typeof(NRulesRule).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"NRules 规则类型 '{type.FullName}' 未继承 NRules.Fluent.Dsl.Rule。");
        }

        return type;
    }

    private static Assembly ResolveAssembly(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new InvalidOperationException($"NRules 无法加载规则程序集 '{name}'：{ex.Message}", ex);
        }
    }
}
