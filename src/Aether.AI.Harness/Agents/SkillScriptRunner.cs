using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// skill 脚本子进程运行器——<see cref="AgentFactory"/> 的脚本执行件，经 MAF
/// <see cref="AgentFileSkillScriptRunner"/> 委托注入 <c>AgentFileSkillsSource</c>。
/// 按扩展名选解释器（.py→python3，.sh→sh，.js→node），以子进程执行脚本并传入 CLI 参数，返回 stdout。
/// 失败时返回含退出码/stderr 的字符串（工具返回字符串契约，不抛异常）。
/// <para>
/// 进程卫生（对齐 SystemTools 的「先杀后读」约定）：stdout/stderr 在 <c>WaitForExit</c> 之前并发读
/// （脚本输出超 ~64KB 管道缓冲会写阻塞 → <c>WaitForExit</c> 死锁）；读不带取消令牌（取消时应返回
/// 残留输出而非抛出丢数据）；任何路径（取消/超时/异常/正常）finally 杀进程树，避免孤儿脚本进程吃 CPU。
/// </para>
/// <para>解释器解析：<c>PythonExecutable</c>（绝对路径）直接跑；相对解释器（python3/sh/node）经登录
/// shell 跑（解 GUI 受限 PATH，与 McpServerRegistry/ShellCommandTool 同一套解法）。CWD 默认用户主目录
/// （避 .app CWD=/）。</para>
/// </summary>
internal sealed class SkillScriptRunner
{
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>执行 .py 脚本的解释器（默认 python3）。由宿主通过 AetherAIOptions.PythonExecutable 设置。</summary>
    public string? PythonExecutable { get; set; }

    public SkillScriptRunner(ILoggerFactory? loggerFactory) => _loggerFactory = loggerFactory;

    /// <summary>
    /// 运行一个 skill 脚本：解析 CLI 参数（数组/对象/整串兼容）→ 选解释器 → 子进程执行 → 返回 stdout。
    /// </summary>
    public async Task<object?> RunAsync(
        AgentFileSkill skill, AgentFileSkillScript script, JsonElement? rawArguments,
        IServiceProvider? serviceProvider, CancellationToken cancellationToken)
    {
        var args = ParseCliArgs(rawArguments);
        var log = _loggerFactory?.CreateLogger<SkillScriptRunner>();
        log?.LogDebug("run_skill_script {Script} raw={Raw} → args={Args}", script.FullPath, rawArguments, string.Join(" ", args));
        if (rawArguments is not null && args.Count == 0)
            log?.LogWarning("run_skill_script 收到的参数未能解析出 CLI 参数（raw={Raw}），脚本将以无参数运行。", rawArguments);
        var interpreter = ResolveInterpreter(script.FullPath);
        var allArgs = new List<string> { script.FullPath };
        allArgs.AddRange(args);

        // 相对解释器经登录 shell 跑（-l source profile 拿完整 PATH）；绝对路径直接跑。
        ProcessStartInfo psi;
        if (Path.IsPathRooted(interpreter))
        {
            psi = new ProcessStartInfo
            {
                FileName = interpreter,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in allArgs) psi.ArgumentList.Add(a);
        }
        else
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(shell))
            {
                var cmd = ShellQuote(interpreter) + " " + string.Join(" ", allArgs.Select(ShellQuote));
                psi = new ProcessStartInfo
                {
                    FileName = shell,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-l");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(cmd);
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = interpreter,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (var a in allArgs) psi.ArgumentList.Add(a);
            }
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi };
            // 重定向并立即关闭 stdin：脚本若读 stdin 拿到 EOF 而非永久阻塞（.app 下 stdin 可能不可读）。
            process.StartInfo.RedirectStandardInput = true;
            if (!process.Start()) return $"无法启动脚本进程：{interpreter}";
            try { process.StandardInput.Close(); } catch { }

            // 必须在 WaitForExit 之前并发读 stdout/stderr：脚本输出超 ~64KB 管道缓冲会写阻塞、
            // 永不退出 → WaitForExit 死锁卡死。不带取消令牌——取消时应返回残留输出而非抛出。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 超时兜底（脚本可能死循环 / 起服务不退）。用户取消经 cancellationToken 即时取消。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 超时：先杀进程树（关管道 EOF）再读残留 stderr，避免 ReadToEndAsync 死锁。
                KillProcessTree(process);
                var partialErr = await stderrTask.ConfigureAwait(false);
                return $"脚本执行超时（120s），已终止进程。\n{Truncate(partialErr)}";
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                return $"脚本退出码 {process.ExitCode}：{Truncate(stderr)}";
            }
            return stdout;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消：finally 已杀进程。返回取消说明（保持工具返回字符串契约；ct 已取消，本轮随即结束）。
            return "脚本执行已被取消（用户停止）。";
        }
        catch (Exception ex)
        {
            return $"脚本执行失败：{ex.Message}";
        }
        finally
        {
            // 任何路径（取消/超时/异常/正常）都杀进程树，避免孤儿脚本进程继续吃 CPU。
            if (process is not null)
            {
                KillProcessTree(process);
                process.Dispose();
            }
        }
    }

    /// <summary>尽力杀掉脚本进程树：先 Kill(true)（含子进程），再 Kill() 单进程兜底。</summary>
    private static void KillProcessTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* 落到下面兜底 */ }
        try { if (!process.HasExited) process.Kill(); }
        catch { /* 进程可能已退出 */ }
    }

    /// <summary>截断脚本输出到 10000 字符，避免超长 stderr 撑爆工具结果。</summary>
    private static string Truncate(string text, int max = 10000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..max] + $"\n... (已截断，总长度 {text.Length} 字符)";
    }

    // —— CLI 参数归一：MAF 传来的 raw JSON 可能是数组（本就 CLI 形）、对象（--key value）、
    //    或整串（agent 把命令行拼成一个字符串）——统一解析成参数列表，并剥掉 agent 可能带进来的
    //    解释器/脚本路径前缀（SKILL.md 的 Bash 写法与 run_skill_script 的模型不匹配，见 StripLeadingPrefix）。

    private static List<string> ParseCliArgs(JsonElement? raw)
    {
        var list = new List<string>();
        if (raw is not { } el) return list;

        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) list.Add(ToArg(item));
                break;
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) list.AddRange(ShellSplit(s));
                break;
            case JsonValueKind.Object:
                // {key: value} → --key value（bool true → 仅 --key 标志，false → 跳过）
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.False) continue;
                    list.Add("--" + prop.Name);
                    if (prop.Value.ValueKind == JsonValueKind.True) continue;
                    list.Add(ToArg(prop.Value));
                }
                break;
        }
        return StripLeadingPrefix(list);
    }

    private static string ToArg(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String) return item.GetString() ?? "";
        if (item.ValueKind == JsonValueKind.Number) return item.GetRawText();
        if (item.ValueKind == JsonValueKind.True) return "true";
        if (item.ValueKind == JsonValueKind.False) return "false";
        return item.ToString();
    }

    /// <summary>
    /// 剥掉 agent 可能带进来的前缀：SKILL.md 里写的是 <c>python "&lt;SQLQ&gt;" asyncSqlQuery ...</c>（Bash 写法），
    /// agent 可能把 <c>python</c> 解释器和 <c>&lt;SQLQ&gt;</c> 占位符 / 脚本路径也塞进参数。run_skill_script 已自带
    /// 解释器 + 脚本路径，这里把它们剥掉，只留真正的子命令 + 参数。
    /// </summary>
    private static List<string> StripLeadingPrefix(List<string> args)
    {
        int i = 0;
        if (i < args.Count && IsPython(args[i])) i++;
        if (i < args.Count && IsScriptRef(args[i])) i++;
        return i == 0 ? args : args.Skip(i).ToList();
    }

    private static bool IsPython(string s)
        => s is "python" or "python3" or "py"
           || s.EndsWith("/python", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith("/python3", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptRef(string s)
        => !string.IsNullOrEmpty(s)
           && (s.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
               || (s.StartsWith('<') && s.EndsWith('>'))            // <SQLQ> 占位符
               || s.Contains("sql_query", StringComparison.OrdinalIgnoreCase)
               || s.Contains('/') || s.Contains('\\'));

    /// <summary>简单 shell 分词（支持单/双引号 + 反斜杠转义），用于 agent 传整条命令串的情况。</summary>
    private static List<string> ShellSplit(string s)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        char? quote = null;
        bool inTok = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is not null)
            {
                if (c == quote.Value) quote = null;
                else if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]);
                else sb.Append(c);
            }
            else if (c == '"' || c == '\'') { quote = c; inTok = true; }
            else if (char.IsWhiteSpace(c)) { if (inTok) { list.Add(sb.ToString()); sb.Clear(); inTok = false; } }
            else { sb.Append(c); inTok = true; }
        }
        if (inTok) list.Add(sb.ToString());
        return list;
    }

    private string ResolveInterpreter(string scriptPath)
    {
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        return ext switch
        {
            ".py" => string.IsNullOrWhiteSpace(PythonExecutable) ? "python3" : PythonExecutable,
            ".sh" => "sh",
            ".js" => "node",
            _ => "sh",
        };
    }

    /// <summary>POSIX 单引号转义：把参数包成 '…'，内部 ' 转义为 '\''。</summary>
    private static string ShellQuote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        return "'" + s.Replace("'", "'\\''") + "'";
    }
}
