namespace Anima.Launcher.Core;

/// <summary>Coalesces display invalidations without throttling durable state or commands.</summary>
public sealed class PresentationRefreshGate
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly object sync = new();
    private int pending;
    private bool visible = true;
    private TimeSpan next;
    public void Request(int flags) { lock (sync) pending |= flags; }
    public void SetVisible(bool value) { lock (sync) visible = value; }
    public TimeSpan? Delay(TimeSpan now)
    {
        lock (sync) return !visible || pending == 0 ? null : next > now ? next - now : TimeSpan.Zero;
    }
    public int Take(TimeSpan now, bool force = false, int mask = int.MaxValue)
    {
        lock (sync)
        {
            if (!visible || pending == 0 || !force && now < next) return 0;
            var result = pending & mask;
            pending &= ~result;
            if (result != 0) next = now + Interval;
            return result;
        }
    }
}
