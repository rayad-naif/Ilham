using Ilham.Core.Emotions;

namespace Ilham.Core.Perception;

/// <summary>
/// What the browser read from the user's own webcam and microphone in the last moment. The heavy lifting
/// (face landmarks, speech) happens in the browser on the user's device; only this small summary is sent to
/// Ilham, so raw video and audio never leave the machine.
/// </summary>
/// <param name="FaceExpression">Dominant facial expression: "happy", "sad", "angry", "surprised", "tired", "neutral", or null if the camera is off.</param>
/// <param name="FaceConfidence">0..1 confidence in that expression.</param>
/// <param name="EyesTired">True if blink rate / eye openness suggests tiredness.</param>
/// <param name="VoiceEnergy">0..1 loudness of recent speech (null if not speaking).</param>
/// <param name="VoicePace">0..1 speaking speed (null if not speaking).</param>
/// <param name="Transcript">What the user just said, if anything (used only for sentiment hints here).</param>
public sealed record PerceptionReport(
    string? FaceExpression, double FaceConfidence, bool EyesTired,
    double? VoiceEnergy, double? VoicePace, string? Transcript);

/// <summary>Translates a <see cref="PerceptionReport"/> into emotional nudges for Ilham.</summary>
public static class PerceptionInterpreter
{
    /// <summary>Produces the feelings a report should stir in her (mood, strength, reason).</summary>
    /// <param name="r">The browser's read of the user.</param>
    public static IEnumerable<(Mood Mood, double Amount, string Reason)> ToFeelings(PerceptionReport r)
    {
        // Only react to a face read she's reasonably sure about.
        if (r.FaceExpression is { } face && r.FaceConfidence >= 0.5)
        {
            double a = 0.25 + 0.35 * r.FaceConfidence; // confident reads move her more
            switch (face.ToLowerInvariant())
            {
                case "happy": yield return (Mood.Happy, a, "he's smiling"); break;
                case "sad": yield return (Mood.Worried, a, "he looks down"); break;
                case "angry": yield return (Mood.Worried, a, "he looks frustrated"); break;
                case "surprised": yield return (Mood.Excited, a * 0.6, "something surprised him"); break;
                case "tired": yield return (Mood.Worried, a, "he looks tired"); break;
            }
        }
        if (r.EyesTired) yield return (Mood.Worried, 0.3, "his eyes look tired — he should rest them");

        // Loud and fast speech reads as agitation; quiet and slow as low energy.
        if (r is { VoiceEnergy: { } energy, VoicePace: { } pace })
        {
            if (energy > 0.7 && pace > 0.6) yield return (Mood.Worried, 0.25, "he sounds worked up");
            else if (energy < 0.25 && pace < 0.35) yield return (Mood.Loving, 0.2, "he sounds quiet and tired");
        }
    }
}
