using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The login poster (ServerOptions.Supporter.Ad). The client asks for its posters with POPUPIMAGE once per world entry
/// from the role screen (LC_FacePhotoHelper._onEnterGameScene → ReqFacePhoto, PopupImageReq language/channel) and opens
/// UI_Sys_ActivityFacePhotoPanel when the answer lists any (LC_DAL_FacePhoto → LC_System_Activity._onPopFacePhoto). The
/// panel downloads imageInfo.imageUrl into its picture and its Go button opens imageInfo.exJump in the browser
/// (Application.OpenURL). The publisher filled this from its operations backend; the research server never answered, so
/// no poster ever showed. An empty list shows nothing.
/// </summary>
public sealed class SupporterAd(IOptions<ServerOptions> options, AccessList access) : Messaging.IMessageModule
{
    public const uint PopupImage = 83271;

    public void Map(Messaging.MessageRoutes routes)
    {
        routes.AdmitRole(id => id == PopupImage); // reads the sender's own status, answers only the sender
        routes.Map(PopupImage, "supporter-ad", async (session, _, _) => await session.SendGameAsync(PopupImage, Reply(session)));
    }

    internal PopupImageRes Reply(GameSession session)
    {
        var all = options.Value;
        var ad = all.Supporter.Ad;
        var res = new PopupImageRes();
        if (!ad.Enabled || ad.ImageUrl.Length == 0 || session.Role is not { } role) return res;
        if (ServerOptions.AccountListed(all.StatusExcludedAccounts, session.Account) || access.IsSupporter(session.Account)) return res;
        if ((session.Progress?.Level ?? role.Level) < ad.MinLevel) return res;
        res.Images.Add(new PopupImageResInfo
        {
            Rank = 1,
            PopImageId = 1,
            Title = "Supporter",
            ImageInfo = new Romsg.PopupImage { ImageUrl = ad.ImageUrl, Language = "en", ExJump = ad.LinkUrl },
        });
        return res;
    }
}
