# Supporter and GM identity

Sol RO3 adds an account-based cosmetic entitlement and a public overhead badge. This is an **inferred house policy**,
not original-server behaviour. It changes no combat stats, GM permissions, account names or stored character names.

`AccessList` supporters receive the **Sol Supporter** portrait frame on every character. Its new SocialOutfitConfig
row is 299001, type 2, using the shipped `chat_img_avatarframe_280001.png` crown-and-jewels artwork. The original
Genesis Canticle and Victory of the Covenant rows and their reward sources are unchanged. No item or shop purchase
grants this entitlement. The derived 5044 view adds it while the account is a supporter; a stored unlock cannot grant
it. It is selected automatically if the player has no saved frame choice. Existing choices remain, and 83480 saves a
supporter's selection normally. Removing supporter status removes access and falls back to the normal default when
the supporter frame was worn. Re-adding status restores access. No database migration or mass mail is needed.

The server publishes custom-data id 9920001, with long values 0 ordinary, 1 supporter or 2 GM. Only explicitly listed
`GmAccounts` get the GM badge, which takes priority over a supporter badge. `GmOpenWhenEmpty` and `GmToolAccounts` do
not label ordinary players GM. The generic custom-data producer rejects this reserved id. This marker conveys no
permissions and exposes no account name. Changes to supporter status refresh the own cosmetics and visible players'
overhead badges within the one-second identity scan. Deferred work checks the role attachment before publishing.

The client hook wraps `LC_HudElement_NameTitle.RefreshAll` to add gold `[Supporter]` or cyan `[GM]` text. It preserves
the original ShowName/masking decisions and name alpha, and listens for custom-data changes so removing a badge
restores the ordinary name. Unknown badge values use the ordinary name. Literal angle brackets in player names are
displayed as angle quotation marks to prevent rich-text colour injection. The raw character name stays unchanged.

Client evidence:

- `SocialOutfitConfig`: type 2 and `_kResource1` select portrait frames; the chosen artwork is shipped but unused by
  the original social-outfit table.
- `Lua_CFG_Base.GetAllIDList` and `Lua_CFG_SocialOutfitConfig._initConfigByType` cache lists; injection clears both
  caches after adding the new row. The row and English texts are restored after config reloads.
- `LC_CustomDataManager.SetUnitCustomData/_pushCustomData` decodes typed values, stores them on the unit and permits
  ids with no original custom handler. EntityBase field 33 supplies initial values; 31018 supplies updates.
- `LC_UnitBase.SetCustomData` stores values without dirtying the HUD for an unknown id. The hook wraps this method
  and calls `RefreshHud` for the identity marker, so live changes also redraw an already visible nameplate.
- `LC_HudElement_NameTitle.RefreshAll` sets Name and NameColor; `LC_MeshUI_NamePlate` applies NameColor to Text_Name.

## Client rollout

The matching client hooks DLL is required for the frame and coloured labels. After registering the cosmetic config,
the hook sends house message 77017 with body byte 1 while a local player exists. This is a capability announcement,
never an entitlement claim. The server sends the new frame id only after that announcement; public profile replies
also check the viewer's capability. Older clients keep ordinary frames and names. The capability belongs to the game
connection and is re-established after reconnecting. Role changes keep the capability while deriving each role's
frame from its own saved choices and the account entitlement.

Build and distribute the matching hooks DLL with the server change through the launcher. Refresh `prebuilt/client`
using `tools/release/pack.ps1 -UpdatePrebuiltClientDlls`; the source stamp includes the hooks, localization and linked
shared rules. Asset bundles and exported publisher data must not be committed or included in the release.

`PlayerIdentityTests` cover ordinary/GM/supporter markers, research/tool account exclusions, legacy client behaviour,
new-client negotiation, multiple characters, frame selection, server restart, revocation, public portraits and
rejection of forged ownership/marker changes on invented data. In-game visual verification remains a separate check.
