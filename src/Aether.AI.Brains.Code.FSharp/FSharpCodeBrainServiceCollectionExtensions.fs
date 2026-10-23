namespace Aether.AI.Brains.Code.FSharp

open System.Runtime.CompilerServices
open Aether.AI.Brains.Code
open Microsoft.Extensions.DependencyInjection

/// Registers precompiled F# code-brain execution without FCS.
[<Extension>]
type FSharpCodeBrainServiceCollectionExtensions =

    [<Extension>]
    static member AddFSharpCodeBrains(services: IServiceCollection) : IServiceCollection =
        services.AddCodeBrains()
