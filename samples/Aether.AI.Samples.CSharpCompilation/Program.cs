using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// 动态 C# 编译：源码来自 ICodeNoteSource（示例里是内存实现，生产里接你的笔记/数据库），
// 编译产物按 id 注册进代码大脑注册表，宿主照常按 id 召唤。
var services = new ServiceCollection();
services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
services.AddSingleton<ICodeNoteSource>(new InMemorySource());
services.AddCSharpBrainCompilation();

using var provider = services.BuildServiceProvider();
await provider.GetRequiredService<ICodeCompilationService>().LoadAsync();

var registry = provider.GetRequiredService<ICodeBrainRegistry>();
if (!registry.TryCreate("dynamic-code", out var brain, out var error))
{
    Console.WriteLine($"编译/注册失败：{error}");
    return;
}

var contents = await brain!.DecideAsync(
    new BrainContext { LatestUser = new ChatMessage(ChatRole.User, "hello") }, default);

Console.WriteLine(contents.OfType<TextContent>().Single().Text); // compiled:hello

/// <summary>代码大脑源码提供者：示例用内存源，生产接笔记/数据库。</summary>
internal sealed class InMemorySource : ICodeNoteSource
{
    private const string Source = """
        using System.Threading.Tasks;
        using Aether.AI.Brains.Code;

        public sealed class DynamicBrain : CodeRuleBrain
        {
            protected override async Task<string> RunAsync()
            {
                await Task.Yield();
                return "compiled:" + UserText;
            }
        }
        """;

    public Task<IReadOnlyList<CodeNote>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CodeNote>>(new[]
        {
            new CodeNote("dynamic-code", "Dynamic Code", Source, "csharp"),
        });
}