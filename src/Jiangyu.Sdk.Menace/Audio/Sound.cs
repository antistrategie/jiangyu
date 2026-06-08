using Il2CppMenace.Audio;

namespace Jiangyu.Game.Audio;

/// <summary>
/// The game's standard UI feedback sounds, so a mod's buttons and tiles click like
/// native ones. The default click sound ids live on the game's <c>AudioConfig</c> and
/// are Stem ids that play themselves.
/// </summary>
public static class Sound
{
    /// <summary>Play the game's default UI left-click sound.</summary>
    public static void Click()
    {
        try
        {
            var config = AudioConfig.Get();
            if (config != null)
                config.SoundDefaultOnLeftClick.Play(1f, 1f);
        }
        catch { }
    }

    /// <summary>Play the game's default UI right-click sound.</summary>
    public static void RightClick()
    {
        try
        {
            var config = AudioConfig.Get();
            if (config != null)
                config.SoundDefaultOnRightClick.Play(1f, 1f);
        }
        catch { }
    }
}
