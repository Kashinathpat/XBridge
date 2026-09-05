using XBridge.Diagnostics;

namespace XBridge;

// Idempotent "tear everything down" hook. Every exit path funnels through RunOnce.
internal static class LifetimeCleanup
{
    private static int _ran;
    private static Action? _handler;

    public static void SetHandler(Action handler) => _handler = handler;

    public static void RunOnce()
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0) return;
        try { _handler?.Invoke(); }
        catch (Exception ex) { try { Log.Error("LifetimeCleanup handler threw", ex); } catch { } }
    }
}
