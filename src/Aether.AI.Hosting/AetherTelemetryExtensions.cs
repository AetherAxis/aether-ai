using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Aether.AI.Hosting;

/// <summary>
/// OTel SDK 配置扩展：订阅 <c>Aether.AI.Harness</c> Activity 源 + 导出器（console/OTLP）。
/// 至少配一个导出器才启用 SDK；agent 的 <c>EnableOpenTelemetry</c> 控制是否发 Activity，
/// 此处控制是否导出。未配导出器时 Activity 仍创建但无 listener（cheap no-op）。
/// </summary>
public static class AetherTelemetryExtensions
{
    public static IServiceCollection AddAetherTelemetry(this IServiceCollection services, Action<OpenTelemetryOptions>? configure = null)
    {
        var opts = new OpenTelemetryOptions();
        configure?.Invoke(opts);
        if (!opts.ConsoleEnabled && string.IsNullOrEmpty(opts.OtlpEndpoint)) return services;

        services.AddOpenTelemetry().WithTracing(t =>
        {
            t.AddSource("Aether.AI.Harness"); // Aether agent.run span + tool 事件
            if (opts.ConsoleEnabled) t.AddConsoleExporter();
            if (!string.IsNullOrEmpty(opts.OtlpEndpoint))
                t.AddOtlpExporter(o => o.Endpoint = new Uri(opts.OtlpEndpoint!));
        });
        return services;
    }
}
