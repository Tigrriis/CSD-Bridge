using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CsdBridge.Core.Agent;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Bridges the background thread that talks to the Claude API and BricsCAD's main
    /// thread, which is the only thread allowed to touch the drawing.
    ///
    /// While a request runs, the AIDRAFT command sits in <see cref="PumpUntil"/> on the
    /// main thread: it executes queued tool calls and progress updates, keeps BricsCAD
    /// and the palette responsive, and watches for Esc. Because all of this happens
    /// inside one command, the document is locked for the duration and the whole AI
    /// request becomes a single UNDO step.
    /// </summary>
    internal sealed class MainThreadPump : IMainThreadInvoker
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        public static MainThreadPump Instance { get; } = new MainThreadPump();

        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            // RunContinuationsAsynchronously: the agent's continuation must not run inline
            // on the main thread when the work completes.
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    tcs.SetResult(work());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        /// <summary>Queue an action for the main thread (fire and forget).</summary>
        public void Post(Action action)
        {
            _queue.Enqueue(action);
            _signal.Set();
        }

        /// <summary>
        /// Run queued work on the calling (main) thread until <paramref name="task"/> finishes.
        /// <paramref name="escapePressed"/> is polled between work items.
        /// </summary>
        public void PumpUntil(Task task, Func<bool> escapePressed, Action onEscape)
        {
            while (!task.IsCompleted)
            {
                Drain();

                // Let BricsCAD repaint and the palette's Stop button respond.
                System.Windows.Forms.Application.DoEvents();

                if (escapePressed())
                {
                    onEscape();
                }

                _signal.WaitOne(25);
            }

            Drain(); // final progress events
        }

        private void Drain()
        {
            while (_queue.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }

    /// <summary>Routes agent progress events through the pump so UI updates happen on the main thread.</summary>
    internal sealed class PumpedProgress : IProgress<AgentEvent>
    {
        private readonly Action<AgentEvent> _handler;

        public PumpedProgress(Action<AgentEvent> handler)
        {
            _handler = handler;
        }

        public void Report(AgentEvent value) => MainThreadPump.Instance.Post(() => _handler(value));
    }
}
