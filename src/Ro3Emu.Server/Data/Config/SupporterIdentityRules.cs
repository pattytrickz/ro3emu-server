using System.Collections.Generic;

namespace Ro3Emu.Shared;

/// <summary>Sol RO3 cosmetic identity policy (inferred), shared with the client hook. No combat attributes.</summary>
public static class SupporterIdentityRules
{
    public const int Ordinary = 0, Supporter = 1, GameMaster = 2;
    public const long BadgeCustomData = 9_920_001;
    public const uint ClientReadyMessage = 77017;
    public const int Frame = 299001, FrameSlot = 2;
    public const long FrameNameText = 99_200_001_001, FrameDescriptionText = 99_200_001_002;
    public const string FrameName = "Sol Supporter";
    public const string FrameDescription = "A thank-you for supporting Sol RO3. Available while your account is a supporter.";
    public const string FrameResource = "chat_img_avatarframe_280001.png";
    public const string SupporterLabel = "[Supporter] ", GmLabel = "[GM] ";

    public static string Label(int badge) => badge == GameMaster ? GmLabel : badge == Supporter ? SupporterLabel : "";

    public static string DisplayName(int badge, string name) => Label(badge) + (name ?? "").Replace("<", "\u2039").Replace(">", "\u203a");

    // Original portrait frames use SocialOutfitConfig type 2 and _kResource1. The artwork is shipped, unused by
    // the original config. This is a new row, so existing earned frames and their reward sources stay intact.
    public static Dictionary<string, object> FrameRow() => new Dictionary<string, object>
        {
            { "_iID", (long)Frame }, { "_iType", (long)FrameSlot }, { "_iName", new long[] { FrameNameText } },
            { "_iDesc", new long[] { FrameDescriptionText } }, { "_iAcquire", System.Array.Empty<long>() },
            { "_iInterval", 0L }, { "_iIsDefault", 0L }, { "_iIsDefaultUnLock", 0L }, { "_iIsDefaultWear", 0L },
            { "_iJobID", 0L }, { "_iSex", 0L }, { "_iLevel", 0L }, { "_iSortID", 998L },
            { "_kResource1", FrameResource }, { "_kResource2", "" }, { "_kResource3", "" }, { "_kResource4", "" },
        };
}
