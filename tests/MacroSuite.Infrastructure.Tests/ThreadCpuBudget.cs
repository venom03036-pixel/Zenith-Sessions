using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Tests;

/// <summary>
/// A speed budget counted in CPU time of the calling thread rather than wall time. A pass that
/// computes past its budget fails; a pass that only waits (blocked, or descheduled because the
/// machine is busy) does not. Use it where a test must catch real UI-thread work, such as a
/// layout pass of the production editor, without failing whenever other processes share the CPU.
/// </summary>
internal static class ThreadCpuBudget
{
    /// <summary>Runs <paramref name="pass"/> and fails if it used more than <paramref name="budget"/> of this thread's CPU.</summary>
    public static ThreadCpuUse Run(string what, TimeSpan budget, Action pass)
    {
        var start = ThreadCpuSample.Take();
        pass();
        return Check(what, budget, start);
    }

    /// <summary>Fails if this thread used more than <paramref name="budget"/> of CPU since <paramref name="start"/>.</summary>
    public static ThreadCpuUse Check(string what, TimeSpan budget, ThreadCpuSample start)
    {
        var use = ThreadCpuSample.Take() - start;
        if (use.Cpu > budget)
            throw new ThreadCpuBudgetExceededException(what, budget, use);
        return use;
    }

    /// <summary>Keeps this thread computing until it has used <paramref name="cpu"/> of CPU (for tests of budgets).</summary>
    public static void Burn(TimeSpan cpu)
    {
        var start = ThreadCpuSample.Take();
        var giveUp = Stopwatch.StartNew();
        while ((ThreadCpuSample.Take() - start).Cpu < cpu)
        {
            if (giveUp.Elapsed > TimeSpan.FromMinutes(1))
                throw new TimeoutException($"This thread could not get {cpu.TotalMilliseconds:0} ms of CPU within a minute.");
        }
    }
}

/// <summary>
/// CPU time and allocation of the calling thread, the wall clock, and the process's garbage
/// collection counts at one moment. Thread CPU advances in clock ticks (about 16 ms).
/// </summary>
internal readonly record struct ThreadCpuSample(long WallTicks, TimeSpan User, TimeSpan Kernel, long Allocated, int Gen0, int Gen1, int Gen2)
{
    public static ThreadCpuSample Take()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
            throw new InvalidOperationException($"GetThreadTimes failed with Win32 error {Marshal.GetLastWin32Error()}.");
        return new(Stopwatch.GetTimestamp(), TimeSpan.FromTicks(user), TimeSpan.FromTicks(kernel),
            GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    public static ThreadCpuUse operator -(ThreadCpuSample end, ThreadCpuSample start) => new(
        Stopwatch.GetElapsedTime(start.WallTicks, end.WallTicks), end.User - start.User, end.Kernel - start.Kernel,
        end.Allocated - start.Allocated, end.Gen0 - start.Gen0, end.Gen1 - start.Gen1, end.Gen2 - start.Gen2);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}

/// <summary>What one pass used: user plus kernel CPU is the budgeted figure; the rest explains it.</summary>
internal readonly record struct ThreadCpuUse(TimeSpan Wall, TimeSpan User, TimeSpan Kernel, long Allocated, int Gen0, int Gen1, int Gen2)
{
    public TimeSpan Cpu => User + Kernel;

    public override string ToString() =>
        $"{Cpu.TotalMilliseconds:0} ms CPU (user {User.TotalMilliseconds:0}, kernel {Kernel.TotalMilliseconds:0}) in {Wall.TotalMilliseconds:0} ms; " +
        $"{Allocated / (1024 * 1024)} MB allocated; collections gen0 {Gen0}, gen1 {Gen1}, gen2 {Gen2}";
}

internal sealed class ThreadCpuBudgetExceededException(string what, TimeSpan budget, ThreadCpuUse use)
    : Exception($"{what} used {use}, over its {budget.TotalMilliseconds:0} ms CPU budget.")
{
    public ThreadCpuUse Use { get; } = use;
}
