using System;
using System.Collections.Generic;
using System.Linq;
using HappyEngine.Base;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The role screen's Delete button. UI_Sys_ChooseRolePanel has one (view slot m_kBTN_Delete), but
    /// UI_Sys_ChooseRolePanelCtrl.OnOpen hides it and binds it to an empty _onClickDelete. This hook shows it while a role
    /// is selected and takes its click: a first click turns the label into "Delete &lt;name&gt;? Click again", a second
    /// within <see cref="ConfirmSeconds"/> sends the original LOGIN_ROLE_DELETE (<see cref="MsgId"/>,
    /// AccountDeleteRoleReq {id}) on the game connection, the way the client sends LOGIN_ROLE_SELECT. The server
    /// (RoleDeletion) deletes only the account's own role and never one in the world. The role then leaves the screen's
    /// list (Lua_AssetManager's m_kPlayer:GetRoleList()) and the controller redraws it (_onRefreshRoleSetData). Its
    /// Lua is reached through <see cref="LuaPatches.GlobalNow"/>, as this screen comes before the game's helpers load.
    /// </summary>
    public static class DeleteRoleButton
    {
        /// <summary>Must equal the server's MsgId.LoginRoleDelete (PBEnumDefine T_LOGIN_ROLE_DELETE).</summary>
        public const uint MsgId = 1008;

        private const string Panel = "UI_Sys_ChooseRolePanel(Clone)";
        private const float ConfirmSeconds = 5f;
        private static long _pendingId;
        private static float _pendingUntil;
        private static TMP_Text _label;
        private static string _labelText;

        public static void Tick()
        {
            var panel = GameObject.Find(Panel);
            if (panel == null) { _pendingId = 0; _label = null; return; }
            var button = panel.GetComponentsInChildren<Button>(true)
                .FirstOrDefault(b => b.name.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0);
            if (button == null) return;
            if (button.GetComponent<Click>() == null)
            {
                button.gameObject.AddComponent<Click>();
                _label = button.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                _labelText = _label != null ? _label.text : null;
                Debug.Log("[Ro3Emu.DeleteRole] Delete button found: " + button.name);
            }
            bool selected = Selected(out _, out _, out _);
            if (button.gameObject.activeSelf != selected) button.gameObject.SetActive(selected);
            if (_pendingId != 0 && Time.unscaledTime > _pendingUntil) Reset();
        }

        private static void OnClick()
        {
            try
            {
                if (!Selected(out long id, out string name, out var ctrl)) return;
                if (_pendingId != id || Time.unscaledTime > _pendingUntil)
                {
                    _pendingId = id;
                    _pendingUntil = Time.unscaledTime + ConfirmSeconds;
                    if (_label != null) _label.text = "Delete " + name + "? Click again";
                    return;
                }
                Reset();
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(MsgId, Request(id));
                Debug.Log("[Ro3Emu.DeleteRole] delete sent for role " + id);
                Forget(ctrl, id);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.DeleteRole] " + e);
            }
        }

        private static void Reset()
        {
            _pendingId = 0;
            if (_label != null && _labelText != null) _label.text = _labelText;
        }

        /// <summary>AccountDeleteRoleReq with only field 3 (id, int64): tag 0x18 and the id as a varint.</summary>
        internal static byte[] Request(long id)
        {
            var bytes = new List<byte> { 0x18 };
            ulong value = unchecked((ulong)id);
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
            } while (value != 0);
            return bytes.ToArray();
        }

        /// <summary>The role screen's controller (the open window with m_iSelectRoleIndex) and its selected role
        /// (m_kData: .id, .name, set by _selectExistingRole).</summary>
        private static bool Selected(out long id, out string name, out Obf_R.Obf_ff ctrl)
        {
            id = 0;
            name = null;
            ctrl = FindController();
            if (!(ctrl?.Obf_RuA<object>("m_kData") is Obf_R.Obf_ff data)) return false;
            id = LuaPatches.ToLong(data.Obf_RuA<object>("id"));
            name = data.Obf_RuA<object>("name") as string ?? "this character";
            return id > 0;
        }

        private static Obf_R.Obf_ff FindController()
        {
            var global = LuaPatches.GlobalNow();
            var ui = global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
            var windows = ui?.Obf_RuA<Obf_R.Obf_Ef>("GetAllWindow")?.Obf_HuA(ui)?.FirstOrDefault() as Obf_R.Obf_ff;
            var next = global?.Obf_RuA<Obf_R.Obf_Ef>("next");
            if (windows == null || next == null) return null;
            object key = null;
            for (int guard = 0; guard < 256; guard++)
            {
                var pair = next.Obf_HuA(windows, key);
                if (pair == null || pair.Length == 0 || pair[0] == null) return null;
                key = pair[0];
                if (pair.Length > 1 && pair[1] is Obf_R.Obf_ff ctrl && ctrl.Obf_RuA<object>("m_iSelectRoleIndex") != null) return ctrl;
            }
            return null;
        }

        /// <summary>Drops the role from the screen's role list and redraws the screen (the server has the final word: a
        /// refused delete shows the role again at the next login).</summary>
        private static void Forget(Obf_R.Obf_ff ctrl, long id)
        {
            var global = LuaPatches.GlobalNow();
            var assets = global?.Obf_RuA<Obf_R.Obf_ff>("Lua_AssetManager");
            var instance = assets?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(assets)?.FirstOrDefault() as Obf_R.Obf_ff;
            var player = instance?.Obf_RuA<Obf_R.Obf_ff>("m_kPlayer");
            var list = player?.Obf_RuA<Obf_R.Obf_Ef>("GetRoleList")?.Obf_HuA(player)?.FirstOrDefault() as Obf_R.Obf_ff;
            var next = global?.Obf_RuA<Obf_R.Obf_Ef>("next");
            var remove = global?.Obf_LuA<Obf_R.Obf_Ef>("table.remove");
            if (list == null || next == null || remove == null) { Debug.LogWarning("[Ro3Emu.DeleteRole] role list not found"); return; }
            object key = null;
            for (int guard = 0; guard < 64; guard++)
            {
                var pair = next.Obf_HuA(list, key);
                if (pair == null || pair.Length == 0 || pair[0] == null) break;
                key = pair[0];
                if (pair.Length > 1 && pair[1] is Obf_R.Obf_ff role && LuaPatches.ToLong(role.Obf_RuA<object>("id")) == id)
                {
                    remove.Obf_HuA(list, key);
                    break;
                }
            }
            ctrl?.Obf_RuA<Obf_R.Obf_Ef>("_onRefreshRoleSetData")?.Obf_HuA(ctrl);
        }

        private sealed class Click : MonoBehaviour, IPointerClickHandler
        {
            public void OnPointerClick(PointerEventData eventData) => OnClick();
        }
    }
}
