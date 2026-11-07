using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace MacroSuite.App.Authoring;

/// <summary>The editor's synchronous authoring boundary. The caller retains the actual task even if the frame exits.</summary>
internal static class ModalPreparationBridge
{
    internal static bool TryComplete<T>(Dispatcher dispatcher, Task<T> work, out T result, out Task observation,
        DispatcherFrame? frame = null)
    {
        dispatcher.VerifyAccess();
        frame ??= new DispatcherFrame();
        T value = default!;
        ExceptionDispatchInfo? failure = null;
        bool delivered = false;
        observation = ObserveAsync(frame);
        if (!delivered && !dispatcher.HasShutdownStarted)
            Dispatcher.PushFrame(frame);
        if (!delivered || dispatcher.HasShutdownStarted)
        {
            result = default!;
            return false;
        }
        failure?.Throw();
        result = value;
        return true;

        async Task ObserveAsync(DispatcherFrame ownedFrame)
        {
            try { value = await work.ConfigureAwait(false); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }

            void Deliver()
            {
                delivered = true;
                ownedFrame.Continue = false;
            }

            if (dispatcher.CheckAccess()) Deliver();
            else if (!dispatcher.HasShutdownStarted)
            {
                try { await dispatcher.InvokeAsync(Deliver).Task.ConfigureAwait(false); }
                catch (OperationCanceledException) when (dispatcher.HasShutdownStarted) { }
            }
        }
    }
}
