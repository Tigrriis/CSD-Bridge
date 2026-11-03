using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using CsdBridge.Core.Agent;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Offline;
using CsdBridge.Core.Tools;

// Usage:
//   csd-bridge [options] ["prompt"]
// With a prompt: runs it once. Without: interactive, one conversation, type 'exit' to quit.
//
// Options:
//   --out <file.dxf>        DXF preview path (default: preview.dxf)
//   --units <name>          Drawing units reported to Claude (default: Millimeters)
//   --text-height <n>       Default text height (default: 2.5)
//   --block <name>          Pretend this block exists (repeatable)
//   --model <id>            Claude model (default: claude-opus-5-5)
//   --effort <level>        auto | quick | normal | thorough, or an API level
//                           low | medium | high | xhigh | max (default: high)
//   --standards <file.md>   Office standards appended to the system prompt
//   --no-fallback           Don't send the server-side refusal fallback

var options = new AgentOptions { HostName = "BricsCAD (offline preview harness)" };
var host = new InMemoryDrawingHost();
string outPath = "preview.dxf";
var promptParts = new List<string>();
EffortMode? effortMode = null;
var effortLevels = new EffortLevels();

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--out": outPath = Next(); break;
        case "--units": host.Units = Next(); break;
        case "--text-height": host.DefaultTextHeight = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--block": host.Blocks.Add(Next()); break;
        case "--model": options.Model = Next(); break;
        case "--effort":
        {
            string value = Next();
            if (EffortPolicy.TryParseMode(value, out EffortMode mode))
            {
                effortMode = mode;
            }
            else
            {
                options.Effort = value;
                effortLevels.Normal = DraftingAgent.NormalizeEffort(value);
            }

            break;
        }
        case "--standards": options.OfficeStandards = File.ReadAllText(Next()); break;
        case "--no-fallback": options.ServerSideFallback = false; break;
        case "-h":
        case "--help":
            Console.WriteLine("csd-bridge [--out preview.dxf] [--units Millimeters] [--model id] [--effort auto|quick|normal|thorough|high] [--standards file.md] [\"prompt\"]");
            return 0;
        default: promptParts.Add(args[i]); break;
    }
}

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")))
{
    Console.Error.WriteLine("Set ANTHROPIC_API_KEY first (or log in with `ant auth login`).");
}

host.PointPicker = prompt =>
{
    Console.Write($"\n  Claude asks: {prompt}\n  Enter x,y (blank = cancel): ");
    string? line = Console.ReadLine();
    var parts = (line ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    return parts.Length == 2
           && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
           && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
        ? new Point2(x, y)
        : null;
};

host.EntitySelector = prompt =>
{
    Console.WriteLine($"\n  Claude asks: {prompt}");
    foreach (var e in host.Entities)
    {
        Console.WriteLine($"    {e.Handle,-6} {e.Spec.Kind,-16} {e.Layer}");
    }

    Console.Write("  Enter handles separated by commas (blank = cancel): ");
    string? line = Console.ReadLine();
    return string.IsNullOrWhiteSpace(line)
        ? null
        : line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
};

var client = new AnthropicClient();
var agent = new DraftingAgent(client, new ToolDispatcher(host), options);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n  Cancelling...");
};

async Task<bool> RunOnce(string prompt)
{
    var progress = new ConsoleProgress();
    string? effort = null;
    if (effortMode is EffortMode mode)
    {
        EffortChoice choice = EffortPolicy.Choose(mode, prompt, 0, effortLevels);
        effort = choice.Effort;
        Console.WriteLine($"  effort: {choice.Describe()} -> {choice.Effort}");
    }

    AgentRunResult result = await agent.RunAsync(prompt, progress, cts.Token, effort);
    Console.WriteLine();
    if (!string.IsNullOrWhiteSpace(result.FinalText) && !progress.PrintedFinal(result.FinalText))
    {
        Console.WriteLine("Claude: " + result.FinalText);
    }

    if (result.Error != null)
    {
        Console.WriteLine($"[{result.Status}] {result.Error}");
    }

    DxfPreviewWriter.Write(host, outPath);
    Console.WriteLine($"  {result.Status}. {result.ToolCalls} tool call(s), {result.Usage}. {host.Entities.Count} entities -> {Path.GetFullPath(outPath)}");
    return result.Status != AgentRunStatus.Failed;
}

if (promptParts.Count > 0)
{
    return await RunOnce(string.Join(" ", promptParts)) ? 0 : 1;
}

Console.WriteLine($"CSD-Bridge offline harness - {options.Model}, effort {(effortMode?.ToString() ?? options.Effort)}, units {host.Units}. Type 'exit' to quit, 'reset' for a new conversation.");
while (true)
{
    Console.Write("\n> ");
    string? line = Console.ReadLine();
    if (line == null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (line.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
    {
        agent.Reset();
        Console.WriteLine("  New conversation (the drawing is kept).");
        continue;
    }

    if (line.Trim().Length > 0)
    {
        await RunOnce(line);
    }
}

Console.WriteLine($"Conversation total: {agent.ConversationUsage}");
return 0;

internal sealed class ConsoleProgress : IProgress<AgentEvent>
{
    private string? _lastText;

    public void Report(AgentEvent e)
    {
        switch (e.Kind)
        {
            case AgentEventKind.Waiting:
                Console.Write(e.Text == "1" ? "  thinking..." : ".");
                break;
            case AgentEventKind.AssistantText:
                Console.WriteLine("\nClaude: " + e.Text);
                _lastText = e.Text;
                break;
            case AgentEventKind.ToolStarted:
                Console.Write($"\n  > {e.Text}");
                break;
            case AgentEventKind.ToolFinished:
                Console.Write(e.IsError ? $"  [error] {Trim(e.Text)}" : $"  ({e.Text})");
                break;
            case AgentEventKind.Notice:
                Console.Write($"\n  ! {e.Text}");
                break;
        }
    }

    public bool PrintedFinal(string finalText) => _lastText != null && finalText.EndsWith(_lastText, StringComparison.Ordinal);

    private static string Trim(string s) => s.Length > 160 ? s.Substring(0, 160) + "..." : s;
}
