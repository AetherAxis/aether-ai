namespace Aether.AI.Harness.FSharp

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Aether.AI.Harness.Agents

/// <summary>
/// F# 代码规则大脑的每步动作（作者契约核心）：
/// <list type="bullet">
/// <item><c>Call(tool, args)</c>：调一个工具——MAF 执行、结果回填后，下一轮 <see cref="IFsRuleBrain.Decide"/>
/// 经 <see cref="BrainCtx.LastResult"/>/线性式 <c>let!</c> 返回值拿到结果。</item>
/// <item><c>Reply(text)</c>：终答文本，终止 agent 循环。</item>
/// </list>
/// DU + exhaustive matching：漏配分支是编译错（YAML/C# 给不了的保证）。
/// </summary>
type Step =
    | Reply of text: string
    | Call of tool: string * args: (string * obj) list

/// <summary>每步求值上下文（<see cref="RuleContext"/> 的 F# 视图，由适配器构造）。</summary>
type BrainCtx =
    { /// 本轮用户输入文本（无则空串）。
      UserText: string
      /// 最新工具结果文本（无则空串）。
      LastResult: string
      /// 本轮已调工具次数（= 线性式的已恢复次数）。
      Step: int
      /// 本轮可用工具名。
      ToolNames: IReadOnlyList<string>
      /// 完整对话历史（含 system / 历史 turn / 本轮工具调用与结果）。
      History: IReadOnlyList<ChatMessage> }

/// <summary>
/// 每步决策式大脑（规则表/状态机）：MAF 每轮调一次 <c>Decide</c>，返回本步动作。
/// 状态可存字段（每次召唤 fresh 实例）。与线性流程式 <see cref="FsCodeRuleBrain"/> 二选一。
/// 实现类须有无参构造（编译器经反射创建）。
/// </summary>
type IFsRuleBrain =
    abstract Decide: ctx: BrainCtx -> Step

/// <summary>F# 作者契约的常用 active patterns。</summary>
[<AutoOpen>]
module FsBrainPatterns =

    /// <summary>正则 active pattern（只返回捕获组，不含整匹配）：
    /// <c>match ctx.UserText with | Regex @"查询\s*(\d+)" [code] -> Call("Query", ["code", box code])</c></summary>
    let (|Regex|_|) (pattern: string) (input: string) : string list option =
        let m = Regex.Match(input, pattern)
        if m.Success then
            Some [ for i in 1 .. m.Groups.Count - 1 -> m.Groups.[i].Value ]
        else
            None

/// <summary>契约内部映射（RuleContext↔BrainCtx、Step→AIContent）。公开只读不便（F# internal module），
/// 供 <see cref="FsRuleBrainAdapter"/> 用。</summary>
module internal FsBrainRuntime =

    /// RuleContext → BrainCtx（F# 视图）。
    let ToCtx (context: RuleContext) : BrainCtx =
        let userText =
            match context.LatestUser with
            | null -> ""
            | m -> m.Text
        let lastResult =
            match context.LatestToolResult with
            | null -> ""
            | r ->
                match r.Result with
                | null -> ""
                | v -> v.ToString()
        { UserText = userText
          LastResult = lastResult
          Step = context.ToolCallsThisTurn
          ToolNames =
            context.AvailableTools
            |> Seq.map (fun t -> t.Name)
            |> ResizeArray
            :> IReadOnlyList<string>
          History = context.History }

    /// Step → AIContent 列表（Reply→TextContent；Call→FunctionCallContent，CallId 即时生成）。
    let ToContents (step: Step) : IReadOnlyList<AIContent> =
        let list = ResizeArray<AIContent>()
        match step with
        | Step.Reply text -> list.Add(TextContent text :> AIContent)
        | Step.Call(tool, args) ->
            let dict = Dictionary<string, obj>()
            for (k, v) in args do
                dict.[k] <- v
            let callId = Guid.NewGuid().ToString("N")
            list.Add(FunctionCallContent(callId, tool, dict) :> AIContent)
        list :> IReadOnlyList<AIContent>

/// <summary>
/// 每步式大脑 → 平台 <see cref="IRuleBrain"/> 的适配器。泛型 + <c>new()</c> 约束：平台编译器
/// （<c>FsRuleScriptCompiler</c>）经 <c>MakeGenericType + Activator</c> 以无参构造创建——
/// 每次召唤 fresh 的 <c>'T</c> 大脑实例（与 C# 代码规则的工厂语义一致）。
/// </summary>
type FsRuleBrainAdapter<'T when 'T: (new: unit -> 'T) and 'T :> IFsRuleBrain>() =

    let brain = new 'T()

    interface IRuleBrain with

        member _.DecideAsync(context: RuleContext, cancellationToken: CancellationToken)
            : ValueTask<IReadOnlyList<AIContent>> =
            let ctx = FsBrainRuntime.ToCtx context
            let contents = FsBrainRuntime.ToContents (brain.Decide ctx)
            ValueTask<IReadOnlyList<AIContent>>(contents)
