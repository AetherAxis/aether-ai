namespace Aether.AI.Harness.FSharp

open System.Collections.Generic
open Aether.AI.Harness.Agents

/// <summary>
/// 线性流程式 F# 代码规则大脑：继承 C# <see cref="CodeRuleBrain"/>——协程驱动器（挂起/恢复/工具执行/
/// 循环终止）全部复用，只加 F# 惯用的 <c>Call</c> sugar（tuple list 参数，免匿名对象）。
/// <para>
/// 作者模型：override <c>RunAsync</c>，在其中
/// <c>task { let! r = this.Call("工具名", ["参数", box 值]) ... return "终答" }</c>——顺序调工具取结果，
/// <c>return</c> 终答（终止 agent 循环）。状态存字段（跨 let! 挂起存活；每次召唤 fresh 实例）。
/// </para>
/// <para>F# task CE 可直接 <c>let!</c> 基类 <c>Call</c> 的返回（<c>CallAwaiter</c> 实现
/// ICriticalNotifyCompletion，满足 FS-1097 GetAwaiter 模式）。与每步决策式 <see cref="IFsRuleBrain"/>
/// （规则表/状态机）二选一。实现类须有无参构造。</para>
/// </summary>
[<AbstractClass>]
type FsCodeRuleBrain() =
    inherit CodeRuleBrain()

    /// <summary>F# 惯用 Call：tuple list 参数（如 <c>["code", box "1"]</c>）→ 字典后转基类协程 Call。
    /// 无参工具传 <c>[]</c>。须在 <c>RunAsync</c> 的 task 里 <c>let!</c> 它——挂起协程、
    /// 把调用交给 MAF 执行，恢复后返回工具结果字符串。</summary>
    member this.Call(tool: string, args: (string * obj) list) : CallAwaitable =
        let dict = Dictionary<string, obj>()
        for (k, v) in args do
            dict.[k] <- v
        base.Call(tool, box dict)
