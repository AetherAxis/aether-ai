using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Aether.AI.Brains.Code.Compilation.FSharp;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aether.AI.Harness.Tests;

public class CodeCompilationCompositionTests
{
    [Fact]
    public void Language_extensions_are_idempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodeNoteSource>(new NoteSource(Array.Empty<CodeNote>()));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddCSharpBrainCompilation();
        services.AddCSharpBrainCompilation();
        services.AddFSharpBrainCompilation();
        services.AddFSharpBrainCompilation();

        using var provider = services.BuildServiceProvider();
        var compilers = provider.GetServices<ICodeBrainCompiler>().ToArray();

        Assert.Equal(2, compilers.Length);
        Assert.Equal(2, compilers.Select(compiler => compiler.Language).Distinct().Count());
        Assert.NotNull(provider.GetRequiredService<ICodeCompilationService>());
        Assert.NotNull(provider.GetRequiredService<ICodeBrainRegistry>());
    }

    [Fact]
    public void Different_compilers_for_same_language_fail_composition()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new CodeCompilationService(
            new NoteSource(Array.Empty<CodeNote>()),
            new CodeBrainRegistry(),
            new ICodeBrainCompiler[] { new FirstCompiler(), new SecondCompiler() },
            NullLogger<CodeCompilationService>.Instance));

        Assert.Contains("Multiple code compilers", exception.Message);
        Assert.Contains("CSharp", exception.Message);
    }

    [Fact]
    public async Task Unknown_language_records_load_error()
    {
        var registry = new CodeBrainRegistry();
        var service = new CodeCompilationService(
            new NoteSource(new[] { new CodeNote("unknown", "Unknown", "source", "python") }),
            registry,
            new ICodeBrainCompiler[] { new FirstCompiler() },
            NullLogger<CodeCompilationService>.Instance);

        await service.LoadAsync();

        Assert.Contains("不支持的代码语言", service.LoadErrors["unknown"]);
        Assert.False(registry.TryCreate("unknown", out _, out var error));
        Assert.Contains("不支持的代码语言", error);
    }

    private sealed class NoteSource(IReadOnlyList<CodeNote> notes) : ICodeNoteSource
    {
        public Task<IReadOnlyList<CodeNote>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(notes);
    }

    private sealed class FirstCompiler : ICodeBrainCompiler
    {
        public CodeBrainLanguage Language => CodeBrainLanguage.CSharp;
        public bool TryCompile(string source, out CompiledCodeBrain? compilation, out string? error)
        {
            compilation = new CompiledCodeBrain("first", static () => new ReplyBrain());
            error = null;
            return true;
        }
        public void UnloadStale(IReadOnlySet<string> activeSourceKeys) { }
    }

    private sealed class SecondCompiler : ICodeBrainCompiler
    {
        public CodeBrainLanguage Language => CodeBrainLanguage.CSharp;
        public bool TryCompile(string source, out CompiledCodeBrain? compilation, out string? error)
        {
            compilation = null;
            error = null;
            return false;
        }
        public void UnloadStale(IReadOnlySet<string> activeSourceKeys) { }
    }

    private sealed class ReplyBrain : IBrain
    {
        public ValueTask<IReadOnlyList<Microsoft.Extensions.AI.AIContent>> DecideAsync(
            BrainContext context, CancellationToken cancellationToken)
            => new(Array.Empty<Microsoft.Extensions.AI.AIContent>());
    }
}
