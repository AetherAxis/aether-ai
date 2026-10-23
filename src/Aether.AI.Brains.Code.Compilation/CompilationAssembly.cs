using System.Reflection;
using System.Runtime.Loader;

namespace Aether.AI.Brains.Code.Compilation;

public sealed class CompilationAssemblyLoadContext : AssemblyLoadContext
{
    public CompilationAssemblyLoadContext() : base(isCollectible: true) { }
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}

public readonly record struct LoadedCompilationAssembly(
    Assembly Assembly,
    CompilationAssemblyLoadContext LoadContext);
