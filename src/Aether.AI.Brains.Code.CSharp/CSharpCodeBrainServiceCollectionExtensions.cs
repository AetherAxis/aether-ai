using Aether.AI.Brains.Code;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Brains.Code.CSharp;

/// <summary>Registers precompiled C# code-brain execution without Roslyn.</summary>
public static class CSharpCodeBrainServiceCollectionExtensions
{
    public static IServiceCollection AddCSharpCodeBrains(this IServiceCollection services)
        => services.AddCodeBrains();
}
