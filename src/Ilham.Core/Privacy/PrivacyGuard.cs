namespace Ilham.Core.Privacy;

/// <summary>What the webcam told us this moment. Deliberately coarse: only owner-vs-not, never <i>who</i>.</summary>
public enum PresenceSignal
{
    /// <summary>The enrolled owner's face is recognised.</summary>
    Owner,
    /// <summary>A face is present but it isn't the owner's.</summary>
    NotOwner,
    /// <summary>No face is visible (empty chair).</summary>
    NoFace,
    /// <summary>The camera is off or unavailable.</summary>
    CameraOff,
}

/// <summary>Whether Ilham is on screen or hidden for privacy.</summary>
public enum Visibility
{
    /// <summary>Visible and active (owner is here).</summary>
    Active,
    /// <summary>Hidden/locked because someone who isn't the owner is at the PC.</summary>
    Hidden,
}

/// <summary>
/// Turns a stream of owner-presence readings into a show/hide decision for Ilham, so she disappears for anyone
/// who isn't you — like your phone locking when it's not your face.
/// </summary>
/// <remarks>
/// <para><b>This is a privacy lock, not surveillance.</b> Its only input is the coarse
/// <see cref="PresenceSignal"/> (owner / not-owner / no-face), and its only output is <see cref="Visibility"/>.
/// It never receives, stores, or reports anything that identifies, describes, or profiles another person. It
/// cannot, by construction — there is nowhere in this type to put such data.</para>
/// <para>Debouncing prevents flicker: a brief glance away or a bad frame won't hide her, and she won't pop
/// back until the owner is seen steadily. When the camera is off the lock does nothing (it can't guess).</para>
/// </remarks>
public sealed class PrivacyGuard
{
    /// <summary>Consecutive non-owner readings required before hiding (≈ a couple of seconds of reads).</summary>
    public int HideAfterReadings { get; init; } = 3;

    /// <summary>Consecutive owner readings required before coming back.</summary>
    public int ShowAfterReadings { get; init; } = 2;

    private int _notOwnerRun, _ownerRun;

    /// <summary>Whether the feature is on (opt-in; off by default).</summary>
    public bool Enabled { get; private set; }

    /// <summary>Current show/hide state.</summary>
    public Visibility State { get; private set; } = Visibility.Active;

    /// <summary>True while she is hidden because someone who isn't the owner was seen (for a later "I hid myself" note to you).</summary>
    public bool HidForIntruder { get; private set; }

    /// <summary>Raised when she should hide. The argument is intentionally empty — there is nothing to report about the other person.</summary>
    public event Action? Hide;

    /// <summary>Raised when she should come back (owner recognised again).</summary>
    public event Action? Show;

    /// <summary>Turns the lock on or off. Turning it off always returns her to visible.</summary>
    /// <param name="enabled">Whether to enforce owner-only visibility.</param>
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled && State == Visibility.Hidden) GoActive();
    }

    /// <summary>Feeds one presence reading and updates the decision.</summary>
    /// <param name="signal">The coarse owner/not-owner reading.</param>
    public void Observe(PresenceSignal signal)
    {
        if (!Enabled) return;

        switch (signal)
        {
            case PresenceSignal.Owner:
                _ownerRun++; _notOwnerRun = 0;
                if (State == Visibility.Hidden && _ownerRun >= ShowAfterReadings) GoActive();
                break;

            case PresenceSignal.NotOwner:
                _notOwnerRun++; _ownerRun = 0;
                if (State == Visibility.Active && _notOwnerRun >= HideAfterReadings) GoHidden();
                break;

            case PresenceSignal.NoFace:
                // An empty chair is not an intruder. Hold the current state; don't reveal to no one, don't hide on nobody.
                _ownerRun = _notOwnerRun = 0;
                break;

            case PresenceSignal.CameraOff:
                // Can't tell who's there, so the lock can't act. Stay as we are.
                _ownerRun = _notOwnerRun = 0;
                break;
        }
    }

    private void GoHidden() { State = Visibility.Hidden; HidForIntruder = true; _notOwnerRun = 0; Hide?.Invoke(); }
    private void GoActive() { State = Visibility.Active; _ownerRun = 0; Show?.Invoke(); }

    /// <summary>Call after you've told the owner she hid herself, so it isn't repeated.</summary>
    public void ClearIntruderFlag() => HidForIntruder = false;
}

/// <summary>
/// Decides, from a webcam frame, only whether the enrolled owner is the one present. Implemented on the user's
/// own device (e.g. a local face-recognition model). By design it returns a <see cref="PresenceSignal"/> and
/// nothing else — no identity, no image, no description of anyone.
/// </summary>
public interface IOwnerRecognizer
{
    /// <summary>True once the owner has enrolled their face.</summary>
    bool IsEnrolled { get; }

    /// <summary>Reads the current frame and reports only owner / not-owner / no-face / camera-off.</summary>
    /// <param name="ct">Cancellation.</param>
    Task<PresenceSignal> ReadAsync(CancellationToken ct = default);
}
