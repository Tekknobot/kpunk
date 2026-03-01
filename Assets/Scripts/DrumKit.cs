using UnityEngine;

[CreateAssetMenu(menuName="KMusic/Drums", fileName="DrumKit")]
public class DrumKit : ScriptableObject
{
    public string kitName = "KIT";
    // index 0..7 => drumId 1..8
    public AudioClip kick;
    public AudioClip snare;
    public AudioClip clap;
    public AudioClip hatClosed;
    public AudioClip hatOpen;
    public AudioClip ride;
    public AudioClip rim;
    public AudioClip crash;

    public AudioClip GetClipByDrumId(int drumId)
    {
        return drumId switch
        {
            1 => kick,
            2 => snare,
            3 => clap,
            4 => hatClosed,
            5 => hatOpen,
            6 => ride,
            7 => rim,
            8 => crash,
            _ => null
        };
    }
}