using System.Collections.Generic;
using System.Text.Json;

namespace CsdBridge.Core.Tools
{
    /// <summary>
    /// An extra set of tools offered to Claude next to the drafting tools, such as the
    /// vertical profile designer or the Civil Site Design dialog tools.
    ///
    /// The tool list and prompt section are part of the cached prompt prefix, so they
    /// must not change for the life of an agent.
    /// </summary>
    public interface IToolExtension
    {
        IReadOnlyList<ToolDefinition> Tools { get; }

        /// <summary>Markdown added to the system prompt, or null.</summary>
        string? PromptSection { get; }

        /// <summary>
        /// True when the tool must run on the CAD main thread (through the agent's
        /// invoker, like the drafting tools). False runs it on the agent's background
        /// thread: the extension does its own marshalling or touches no CAD state.
        /// </summary>
        bool RunsOnMainThread(string toolName);

        /// <summary>Run a tool. Should report problems as error outcomes rather than throw.</summary>
        ToolOutcome Execute(string toolName, JsonElement input);

        /// <summary>Short progress text for the CAD UI ("reading the Grading dialog"), or null for the tool name.</summary>
        string? DescribeCall(string toolName, JsonElement input);
    }
}
