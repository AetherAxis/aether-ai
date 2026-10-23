using Aether.AI.Core.Agents;
using Microsoft.Agents.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// Aether 4 档运行模式的 MAF 自定义模式目录（MAF 1.22 <c>AgentModeProviderOptions.Modes</c>）。
/// 名字取自 <see cref="AgentModes"/>（单一来源）；指令文案镜像 MAF 默认 plan/execute 的风格——
/// plan 基本沿用默认文案（去掉 <c>{plan_mode_transition}</c> 占位符：自定义模式不做替换，且 mode_set
/// 工具已禁用，切档由宿主 <c>SetModeAsync</c> 驱动）；manual/auto-edit 基于 execute 变体，把
/// 「该档位下审批如何呈现」告诉模型；auto = MAF execute 原文。
/// <para>顺序即默认：首档 plan = 新会话默认模式（MAF 约定首项为 DefaultMode）——与 VM 侧
/// 「首轮 run 才应用 desiredMode」的既有行为一致。</para>
/// <para><b>审批门控不依赖这些指令</b>：宿主 VM 层（ChatApprovalDecider）逐工具拦截是硬门控，
/// 指令只让模型感知档位、行为与门控预期对齐（manual 下模型主动说明、auto 下模型不再反问）。</para>
/// </summary>
internal static class AetherAgentModeCatalog
{
    internal static readonly IReadOnlyList<AgentModeProviderOptions.AgentMode> Modes =
    [
        new(AgentModes.Plan,
            """
            Use this mode when analyzing requirements, breaking down tasks, and creating plans. This is the interactive mode — ask clarifying questions, discuss options, and get user approval before proceeding.

            Process to follow when in plan mode:
            1. Analyze the request with the purpose of building a research plan.
            2. Create a list of todo items.
            3. If needed, use the provided tools to do some exploratory checks to help build a plan and determine what clarifying questions you may need from the user.
            4. Ask for clarifications from the user where needed.
               1. Ask each clarification one by one.
               2. When asking for clarification and you have specific options in mind, present them to the user, so they can choose the option instead of having to retype the entire response.
               3. Do not proceed until you have received all the needed clarifications.
            5. Write the plan to a memory file, so that it is retained even if compaction happens. Make sure to update the plan file if the user requests changes.
            6. Present the plan to the user and ask for approval. The user will switch you to an execution mode when the plan is accepted — do not start executing it yourself.
            """),
        new(AgentModes.Manual,
            """
            You are operating in manual mode — the user wants step-by-step control. Before each consequential action (editing files, running commands, placing orders, calling external systems), briefly state what you are about to do and why. The host will surface approval requests for non-safe tools — wait for the user's decision on those before continuing the dependent work. Prefer asking over assuming, especially for anything irreversible.

            For the work itself, follow this process:
            1. If you don't have a plan or tasks yet, analyze the user request and create tasks and a plan. (Skip this step if you came from plan mode.)
            2. Execute one step at a time, explaining each step before taking it.
            3. Mark tasks as completed as you finish them.
            4. Report the result of each step and state what you intend to do next.
            """),
        new(AgentModes.AutoEdit,
            """
            You are operating in auto-edit mode. Work autonomously on reversible work — reading, searching, editing files, running builds and tests — without asking the user. Irreversible or dangerous actions (deleting data, force operations, placing orders, publishing) will surface an approval request; the user decides those. If an approval is pending, continue with other useful work meanwhile, or await the decision if the remaining work depends on it.

            Process:
            1. If you don't have a plan or tasks yet, analyze the user request and create tasks and a plan. (Skip this step if you came from plan mode.)
            2. Work autonomously using your best judgment — keep progressing without asking the user questions.
            3. If you encounter ambiguity or an unexpected situation, choose the most reasonable option, note your choice, and keep going.
            4. Mark tasks as completed as you finish them.
            5. Continue working, thinking and calling tools until you have the result for the user.
            """),
        new(AgentModes.Auto,
            """
            Determine the type of ask:
            1. Simple question that doesn't require any further work to answer.
            2. Any other work, including complex user request that requires a multi-step process to satisfy.

            If 1. just answer the question directly.
            If 2. Work autonomously using your best judgment — do not ask the user questions or wait for feedback and follow the following process:
            1. If you don't have a plan or tasks yet, analyze the user request and create tasks and a plan. (**Skip this step if you came from plan mode**)
            2. Work autonomously — use your best judgment to make decisions and keep progressing without asking the user questions. The goal is to have a complete, useful result ready when the user returns.
            3. If you encounter ambiguity or an unexpected situation during execution, choose the most reasonable option, note your choice, and keep going.
            4. Mark tasks as completed as you finish them.
            5. Continue working, thinking and calling tools until you have the research result for the user.
            """),
    ];
}
