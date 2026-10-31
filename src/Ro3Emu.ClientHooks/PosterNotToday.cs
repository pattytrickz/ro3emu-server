using System;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// "Don't show again today" for the login poster. The server's Game.SupporterAd answers the client's POPUPIMAGE
    /// request (LC_FacePhotoHelper.ReqFacePhoto, sent at each world entry from the role screen) and the client shows the
    /// poster in UI_Sys_ActivityFacePhotoPanel, which has no such box in this build. One is added in the picture's
    /// bottom-left corner; ticked, ReqFacePhoto (replaced by a C# wrapper, looked up by name at each call) skips the
    /// request until the player's local day ends. Unticking brings the poster back at the next world entry.
    /// </summary>
    public static class PosterNotToday
    {
        private static readonly string[] Panels = { "UI_Sys_ActivityFacePhotoPanel(Clone)", "UI_Sys_ActivityFacePhotoPanel_PC(Clone)" };
        private const string BoxName = "Ro3Emu_NotToday";
        private const string Label = "Don't show again today";
        private const string PrefKey = "Ro3Emu.PosterSnoozedDay";

        public delegate void NoArgs();
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly NoArgs RequestWrapper = Request;
        private static Obf_R.Obf_Ef _request;

        private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static bool Snoozed => PlayerPrefs.GetString(PrefKey, "") == Today;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff helper)
        {
            var original = helper?.Obf_RuA<Obf_R.Obf_Ef>("ReqFacePhoto");
            if (original == null)
            {
                Debug.LogWarning("[Ro3Emu.Poster] LC_FacePhotoHelper.ReqFacePhoto not found; the poster shows at every world entry");
                return;
            }
            _request = original;
            helper.Obf_luA<string, NoArgs>("ReqFacePhoto", RequestWrapper);
            Debug.Log("[Ro3Emu.Poster] poster request wrapped (snoozed today: " + Snoozed + ")");
        }

        private static void Request()
        {
            try
            {
                if (Snoozed)
                {
                    Debug.Log("[Ro3Emu.Poster] skipped: \"" + Label + "\" ticked");
                    return;
                }
                _request?.Obf_HuA();
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Poster] request failed: " + e);
            }
        }

        /// <summary>From UiTweaks.Tick (every 10th frame).</summary>
        public static void Tick()
        {
            foreach (var name in Panels)
            {
                var panel = GameObject.Find(name);
                if (panel != null && panel.transform.Find(BoxName) == null && !panel.GetComponentsInChildren<Transform>(true).Any(t => t.name == BoxName))
                    Add(panel.transform);
            }
        }

        private static void Add(Transform panel)
        {
            var pictures = panel.GetComponentsInChildren<RawImage>(true);
            var picture = pictures.FirstOrDefault(r => r.name.Contains("Activity_Operation"))
                ?? pictures.OrderByDescending(r => r.rectTransform.rect.width * r.rectTransform.rect.height).FirstOrDefault();
            if (picture == null) return;
            var font = panel.GetComponentsInChildren<TMP_Text>(true).Select(t => t.font).FirstOrDefault(f => f != null) ?? TMP_Settings.defaultFontAsset;

            var root = Rect(BoxName, picture.transform, new Vector2(0f, 0f), new Vector2(20f, 20f), new Vector2(330f, 48f));
            var backing = root.gameObject.AddComponent<Image>();
            backing.color = new Color(0f, 0f, 0f, 0.6f);
            var border = Rect("Box", root, new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(28f, 28f));
            border.gameObject.AddComponent<Image>().color = Color.white;
            var well = Rect("Well", border, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f));
            well.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.14f, 1f);
            var tick = Rect("Tick", border, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(14f, 14f));
            var mark = tick.gameObject.AddComponent<Image>();
            mark.color = new Color(0.91f, 0.18f, 0.22f, 1f);
            var text = Rect("Label", root, new Vector2(0f, 0.5f), new Vector2(52f, 0f), new Vector2(270f, 40f));
            var label = text.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            label.text = Label;
            label.fontSize = 22f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableWordWrapping = false;
            foreach (var g in root.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = g == backing;

            var click = root.gameObject.AddComponent<Click>();
            click.Mark = mark;
            mark.enabled = Snoozed;
            Debug.Log("[Ro3Emu.Poster] \"" + Label + "\" added to " + panel.name + " on " + picture.name);
        }

        /// <summary>A child rect anchored and pivoted at <paramref name="anchor"/>, offset by <paramref name="at"/>.</summary>
        private static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 at, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = at;
            rect.sizeDelta = size;
            rect.SetAsLastSibling();
            return rect;
        }

        private sealed class Click : MonoBehaviour, IPointerClickHandler
        {
            public Image Mark;

            public void OnPointerClick(PointerEventData eventData)
            {
                bool snooze = !Snoozed;
                if (snooze) PlayerPrefs.SetString(PrefKey, Today);
                else PlayerPrefs.DeleteKey(PrefKey);
                PlayerPrefs.Save();
                if (Mark != null) Mark.enabled = snooze;
                Debug.Log("[Ro3Emu.Poster] \"" + Label + "\" " + (snooze ? "ticked" : "unticked"));
            }
        }
    }
}
