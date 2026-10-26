namespace Anima.Launcher.Core;

/// <summary>Time-weighted transfer speed for display only; transport health keeps its own clock.</summary>
public sealed class DownloadSpeedSmoother
{
    private double? speed;
    private TimeSpan last;
    public double? Sample(string? phase, double? instantaneous, TimeSpan elapsed)
    {
        if (phase != "Downloading" || instantaneous is not { } sample || !double.IsFinite(sample) || sample < 0)
        { Reset(); return null; }
        if (speed is null || elapsed < last) speed = sample;
        else
        {
            var weight = 1 - Math.Exp(-(elapsed - last).TotalSeconds / 2);
            speed += weight * (sample - speed.Value);
        }
        last = elapsed;
        return speed;
    }
    public void Reset() { speed = null; last = TimeSpan.Zero; }
}
