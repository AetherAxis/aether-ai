using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var services = new ServiceCollection();
services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
services.AddSingleton<ICodeNoteSource>(new InMemorySource());
services.AddCSharpBrainCompilation();
services.AddCSharpBrainCompilation();
using var provider = services.BuildServiceProvider();
if (provider.GetServices<ICodeBrainCompiler>().Count() != 1)
    throw new InvalidOperationException("C# compiler registration is not idempotent.");

await provider.GetRequiredService<ICodeCompilationService>().LoadAsync();
var registry = provider.GetRequiredService<ICodeBrainRegistry>();
if (!registry.TryCreate("dynamic-code", out var brain, out var error))
    throw new InvalidOperationException(error ?? "Compiled brain was not registered.");

var output = await brain!.DecideAsync(new BrainContext
{
    LatestUser = new ChatMessage(ChatRole.User, "hello"),
}, default);
var text = AssertSingleText(output);
if (text != "compiled:hello") throw new InvalidOperationException($"Unexpected output: {text}");
Console.WriteLine("CSharpCompilation:PASS");

static string AssertSingleText(IReadOnlyList<AIContent> contents)
{
    var texts = contents.OfType<TextContent>().ToArray();
    if (texts.Length != 1) throw new InvalidOperationException($"Expected one text result, found {texts.Length}.");
    return texts[0].Text;
}

sealed class InMemorySource : ICodeNoteSource
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
