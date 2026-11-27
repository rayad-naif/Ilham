using Ilham.Core.Guardian;

namespace Ilham.App;

/// <summary>
/// Ilham's heartbeat: every 30 seconds it asks the OS how long the user has been idle, feeds the guardian,
/// and — if a health concern escalates far enough — carries out the action she warned about (lock/shutdown).
/// The spoken warnings are produced by the chat UI, which polls <c>/api/state</c>; this loop is the part that
/// actually acts, so she follows through even if the window isn't focused.
/// </summary>
public sealed class GuardianLoop(IlhamHost host, ILogger<GuardianLoop> log) : BackgroundService
{
    /// <summary>How often the guardian ticks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        int persistCountdown = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var profile = host.Profiles.Load();
                TimeSpan idle = host.PcActions.Idle();
                GuardianAlert? alert = host.Guardian.Tick(profile, idle);

                if (alert is { Action: not GuardianAction.None })
                {
                    log.LogInformation("Ilham guardian acting: {Action} (level {Level}) — {Why}", alert.Action, alert.Level, alert.Situation);
                    if (alert.Action == GuardianAction.Lock) host.PcActions.Lock();
                    else if (alert.Action == GuardianAction.Shutdown) host.PcActions.ScheduleShutdown(alert.CountdownSeconds, "Ilham: so jao please. Override password se cancel kar sakte ho.");
                }

                if (++persistCountdown >= 4) { host.PersistCounters(); persistCountdown = 0; } // save screen-time every ~2 min
            }
            catch (Exception ex) { log.LogError(ex, "Guardian tick failed"); }
        }
    }
}
