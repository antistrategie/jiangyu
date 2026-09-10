using Il2CppMenace.Conversations;
using UnityEngine;

namespace Jiangyu.Game.Ui;

/// <summary>The standing artwork stored on a speaker template.</summary>
public enum StandingPortrait
{
    Left,
    Right,
    Inactive,
}

/// <summary>Standing artwork for native and mod-created UI.</summary>
public static class Portraits
{
    private static Action<SpeakerTemplate, StandingPortrait> _loadStanding;

    /// <summary>Bound by the loader to resolve deferred template assets on the main thread.</summary>
    public static void BindStandingLoader(Action<SpeakerTemplate, StandingPortrait> load) => _loadStanding = load;

    /// <summary>Get a speaker's standing artwork, loading an opted-in mod's texture on
    /// first use. Call from the main thread when building or updating visible UI.</summary>
    public static Texture2D GetStanding(SpeakerTemplate speaker, StandingPortrait portrait = StandingPortrait.Right)
    {
        if (speaker == null)
            return null;
        _loadStanding?.Invoke(speaker, portrait);
        return portrait switch
        {
            StandingPortrait.Left => speaker.StandLookLeftImage,
            StandingPortrait.Right => speaker.StandLookRightImage,
            StandingPortrait.Inactive => speaker.StandLookRightInactiveImage,
            _ => throw new ArgumentOutOfRangeException(nameof(portrait)),
        };
    }
}
