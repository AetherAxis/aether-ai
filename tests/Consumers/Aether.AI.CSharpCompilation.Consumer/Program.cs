using Aether.AI.Brains.Code.Compilation.CSharp;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddCSharpBrainCompilation();
