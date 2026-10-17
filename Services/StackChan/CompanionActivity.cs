using System;
using System.Threading;

namespace Kil0bitSystemMonitor.Services.StackChan;

/// <summary>Counts overlapping application work without carrying prompts, notes or audio.</summary>
public static class CompanionActivity
{
    private static readonly object Gate = new();
    private static int _ai;
    private static int _meeting;
    public static event Action? Changed;

    public static bool IsActive(StackChanActivity activity)
    {
        lock (Gate) return (activity == StackChanActivity.Ai ? _ai : _meeting) > 0;
    }

    public static IDisposable Enter(StackChanActivity activity)
    {
        lock (Gate)
        {
            if (activity == StackChanActivity.Ai) _ai++;
            else _meeting++;
        }
        Publish();
        return new Lease(activity);
    }

    private sealed class Lease(StackChanActivity activity) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                if (activity == StackChanActivity.Ai) _ai--;
                else _meeting--;
            }
            Publish();
        }
    }

    private static void Publish()
    {
        if (Changed is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch { /* Companion UI cannot interrupt application work. */ }
    }
}
