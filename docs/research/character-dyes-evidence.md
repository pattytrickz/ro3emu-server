# Character dyes and GM aura

This opt-in Sol feature is inferred house policy, not a recreation of a publisher dye system. Enable it with
`Ro3Emu:CharacterAppearanceEnabled=true`; the default is false. `Ro3Emu:CharacterDyePrice` defaults to 1,000 Zeny.
It requires matching client hooks. This is a PTR implementation: armour coverage is limited to female Novice cloth
panels, and broader class, sex, LOD and gameplay visual validation remains a release gate.

## Purchases and persistence

Eight colours plus Original are offered as bound, single-use hair and class-armour dyes by the Dye Merchant beside
Prontera's central fountain (map 10100, x 164 / z 190, grounded on the nav mesh). This is an additional NPC with its
own catalogue; existing Kafra staff and shop stock are unchanged. Its normal level-10 shop gate remains in force. The cloned
item shape comes from ItemConfig 1920003; its original actions, rewards, trade/storage and shortcut flags are cleared.
Its subtype is changed to cosmetic consumable 210, which PublicParamConfig.PCClickUseItemType admits for the PC bag
Use action; the template subtype 218 would silently refuse that path.
No publisher rows or textures are distributed. Shared rules inject matching identifiers, prices and descriptions on
both sides. LC_ItemHelper's generic item-use route sends 31323; `_iNeedConfirm` requests the existing confirmation.

Merchant id 993001 is used in NPCConfig, NPCInteractConfig and ShopConfig, with commodity class 993001. The model is
cloned from resident NPCConfig 100002 and the shop from ShopConfig 10; commodity shapes come from row 113. The "Buy
dyes" interaction follows DialogueConfig 40001402's action 25 (OpenFunctionUI) into UI_Sys_ShopNpcPanel with the new
shop and class ids. This adds no publisher assets. Old clients do not receive the unknown NPC template: it is published
after the appearance capability handshake, and subsequent map loads and reconnects keep that visibility boundary.

Client capability 77018/version 1 is sent only after the item rows exist. The hook rechecks the current config table because the client replaces it during login. Before that handshake, the server refuses
dye purchases and omits saved dye items from 5009. After it, a full inventory sync reveals them. The self-only price
marker 9930002 advertises the server's enabled price or zero; it controls client shop injection.

`role.character_appearance` holds Hair, Armour and Aura. Item consumption and selection commit atomically using the
wardrobe economy family before any selection is published. Failed persistence, invalid parameters, unsupported bodies,
replayed spent GUIDs and selecting the same colour cannot consume another bottle. Reset dyes restore original colours.
Selections travel in custom marker 9930001 on entity creation and nearby-player updates. Generic custom-data requests
cannot write either reserved marker. The economy reader classifies this JSON as cosmetic selections, not a balance;
the dye receipt records the consumed item.

## Client lifecycle and outfit boundary

LC_UnitBase.GetVisibleData and RD_VisibleBase.GetAllRenders supply the active player's materials. A component lives
on the unit GameObject, tracks renderer ownership and restores copies on removal, disable or destruction. Material
replacement on the same renderer triggers reapplication. The saved armour choice is suppressed whenever
`m_kCurUseAppearances[4]` is nonzero, even if an outfit reuses a default texture. Removing the cosmetic restores the
saved choice. Unverified body texture names are never recoloured.

`!aura on` and `!aura off` require an explicitly configured GM, independently of loopback tool privileges or an open
research realm. The client also requires the authoritative GM badge. Removing GM status publishes a cleared aura
marker. Rendering checks the unit's face-mask property, hide masks and stealth state (`enum_showStealthState.None=0`
in Lua_GameDefine), as well as body visibility. This grants no stats and does not replace wardrobe effects.

## Rendering evidence

The current client's `RD_Renderer.GetRendererData().partType` distinguishes `RoleModelPartType.Hair`, `Body`, and
`Head`. `GetMaterials()` returns the instantiated materials used by that renderer. `SetPartColor` writes the
material's `_BaseColor`, also used by game effects; the prototype therefore replaces `_BaseMap` with an owned texture
copy instead. It preserves painted value/shading and alpha while changing hue/saturation. Reset, disable and destroy
restore the original texture and release the copy. Shared source textures are never written.

The female default Novice body uses `Model_Body_Novice_F_LOD0`. Its atlas contains exposed skin, clothing, metal,
belts and boots. A whole-body colour multiplication is unsuitable. `ClassArmourMask` contains conservative authored
UV regions for two cloth panels in this one model. It leaves the skin island and trim unchanged. Unlisted models,
LODs and cosmetic textures fail closed. These initial rectangular regions are a feasibility mask, not finished
outfit artwork. Additional classes, sexes and LODs need individually verified masks before enabling purchases.

The GM aura uses original blue-and-cyan flame particles, a generated teardrop silhouette and a white-core colour/fade
curve. Up to 64 camera-facing particles rise from a narrow ring around the feet, scale with the body, and follow it.
The resident `Sprites/Default` shader draws the soft silhouette. The shipped particle module strips MinMaxGradient's
Gradient constructor and setters but retains its serialized m_Mode and m_GradientMax fields, which the hook sets
explicitly. Hiding or removing the aura clears owned particles, material and texture; it does not occupy a wardrobe
slot or use a combat buff. Its final visual density and performance still need broader gameplay review.

## Running the preview

Use an isolated client copy patched to the PTR endpoint. Before launching with ClientTools, set
`RO3EMU_CLIENT_PROBE` to an existing local directory. The hook checks for `request.txt` there, consumes it once, and
writes `probe.log`. Commands use literal TAB separators:

| Command | Purpose |
| --- | --- |
| `models` | List active renderer IDs, part types, materials and shader textures |
| `dye<TAB>rendererId<TAB>red<TAB>green<TAB>blue` | Preview hair or verified class-armour cloth; channels 0..1 |
| `reset<TAB>rendererId` | Restore that renderer's original texture |
| `aura<TAB>rendererId<TAB>on` / `off` | Show/remove a local aura preview |
| `textures<TAB>rendererId` | Export current textures for inspection outside git |
| `capture<TAB>basename` | Capture the actual game framebuffer |
| `find<TAB>name` | Find active hierarchy paths for UI inspection |
| `use-bag-item<TAB>ownedGuid` | Exercise the existing bag Use action, including its confirmation |
| `ui` | List active input fields, buttons and labels; input-field text is excluded |
| `fill<TAB>exactHierarchyPath<TAB>value` | Fill a test UI field; the value is not logged |
| `click<TAB>exactHierarchyPath` | Dispatch a click to a uniquely matching active UI element |

Wait until the previous request has been consumed before writing the next. Keep credentials, screenshots, exported
textures and logs outside the repository. The probe provides no server authorization. Use dedicated test accounts.

## Validation boundaries

Synthetic integration tests cover purchase/consume/relog, viewer notification, failed-save rollback, legacy inventory
negotiation, unsupported bodies, duplicate and multi-use rejection, disabled rollout, reserved-marker forgery and GM
revocation. Mask tests check the supported cloth regions and exclusion of skin and unverified models.

Finland PTR checks use dedicated accounts and an isolated reconstructed client copy. Purchases and consumption were
verified through the normal protocol, with the resulting hair and default cloth colours visible in the rendering
client. Equipping cosmetic clothing preserved its original colours; removing it restored the saved cloth dye. The
server-authorized blue flame aura was visible after reconnect. One rendering client plus a protocol observer is
available; this does not establish compatibility with an unmodified publisher build or two independent rendering
clients. The PC bag Use helper displayed "Use Violet Hair Dye?"; confirming consumed the bottle once, saved palette 4
and recoloured the character's hair. Broader QA must cover hairstyle and class changes,
mounts, death/revival, stealth/masking, graphics-quality/LOD changes and multiple visible GMs before release.

Additional default class armour requires individually verified masks for each body/LOD. Do not expand the allowlist
or enable paid applications for those bodies from a generic texture-name pattern. Cosmetic outfits remain excluded.
