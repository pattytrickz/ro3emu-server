using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Sol's flexible seller price editor (server FlexibleStallPricing must be enabled).
    /// UI_Com_Stall_Available_ShelvesWidgetCtrl and UI_Com_Stall_Available_TipWidgetBaseCtrl already have
    /// SetPrice and a bounded keypad for Top items; ordinary items instead use SetPriceByIndex.
    /// Reuse the numeric path for new listings and seller edits, never BuyerView.
    /// The PC adjustment widget additionally needs maxNum and setNumHandler to enable its text input.
    /// No TradeConfig rows, currencies, buyer controls, or item eligibility are changed.
    /// </summary>
    public static class StallPrices
    {
        public delegate void SetDataMethod(Obf_R.Obf_ff self, Obf_R.Obf_ff data);
        public delegate bool SetPriceMethod(Obf_R.Obf_ff self, double price);

        // xLua keeps delegates weakly; these must survive for the life of the LuaEnv.
        private static readonly SetDataMethod SetDataWrapper = SetData;
        private static readonly SetDataMethod TipDataWrapper = SetTipData;
        private static readonly SetPriceMethod SetPriceWrapper = SetPrice;
        private static Obf_R.Obf_Ef _original, _originalTip, _bind;
        private static long _sellerOperation, _buyerOperation, _limit;

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff controller, Obf_R.Obf_ff tip, Obf_R.Obf_ff parameters)
        {
            var original = controller?.Obf_RuA<Obf_R.Obf_Ef>("SetData");
            var originalTip = tip?.Obf_RuA<Obf_R.Obf_Ef>("SetData");
            var bind = global.Obf_RuA<Obf_R.Obf_Ef>("Bind");
            var operations = global.Obf_RuA<Obf_R.Obf_ff>("enum_StallOperateType");
            long limit = parameters?.Obf_RuA<long>("TradeLimitPrice") ?? 0;
            if (original == null || originalTip == null || bind == null || operations == null || limit <= 0)
            {
                Debug.LogWarning("[Ro3Emu.StallPrices] seller editor unavailable; original price controls retained");
                return;
            }
            _original = original;
            _originalTip = originalTip;
            _bind = bind;
            _sellerOperation = operations.Obf_RuA<long>("sellerStallOut");
            _buyerOperation = operations.Obf_RuA<long>("BuyerView");
            _limit = limit;
            controller.Obf_luA<string, SetDataMethod>("SetData", SetDataWrapper);
            tip.Obf_luA<string, SetDataMethod>("SetData", TipDataWrapper);
            Debug.Log("[Ro3Emu.StallPrices] flexible seller prices enabled, maximum " + _limit);
        }

        private static void SetData(Obf_R.Obf_ff self, Obf_R.Obf_ff data)
        {
            _original.Obf_HuA(self, data);
            if (self.Obf_RuA<long>("m_eOpType") != _sellerOperation) return;
            Configure(self, data, "_onOpenKeyBoard");
        }

        private static void SetTipData(Obf_R.Obf_ff self, Obf_R.Obf_ff data)
        {
            _originalTip.Obf_HuA(self, data);
            // New-listing tips have no opType. Buyer tips must remain read-only.
            var operation = self.Obf_RuA<object>("m_iOpType");
            if (operation != null && Convert.ToInt64(operation) == _buyerOperation) return;
            Configure(self, data, "OnOpenKeyBoard");
        }

        private static void Configure(Obf_R.Obf_ff self, Obf_R.Obf_ff data, string keyboardMethod)
        {
            var widget = self.Obf_RuA<Obf_R.Obf_ff>("m_kPriceWidget");
            var settings = widget?.Obf_RuA<Obf_R.Obf_ff>("m_kData");
            if (settings == null) return;
            var priceData = data.Obf_RuA<Obf_R.Obf_ff>("priceData");
            var stallData = data.Obf_RuA<Obf_R.Obf_ff>("stallData");
            long price = stallData?.Obf_RuA<long>("price") ?? 0;
            if (price <= 0) price = priceData?.Obf_RuA<long>("iStandardPrice") ?? 1;
            // Only this editor's numeric branch flag: never change LC_StallHelper.IsTopItem or TradeConfig.
            self.Obf_luA<string, bool>("m_bTop", true);
            settings.Obf_luA<string, bool>("bTop", false);
            settings.Obf_luA<string, long>("maxNum", _limit);
            settings.Obf_luA<string, object>("setNumHandler", _bind.Obf_HuA(self, SetPriceWrapper)[0]);
            var setter = _bind.Obf_HuA(self, self.Obf_RuA<Obf_R.Obf_Ef>("SetPrice"))[0];
            settings.Obf_luA<string, object>("inputClickHandler",
                _bind.Obf_HuA(self, self.Obf_RuA<Obf_R.Obf_Ef>(keyboardMethod), setter, _limit)[0]);
            widget.Obf_RuA<Obf_R.Obf_Ef>("SetData").Obf_HuA(widget, settings);
            SetPrice(self, Math.Max(1, Math.Min(_limit, price)));
        }

        private static bool SetPrice(Obf_R.Obf_ff self, double price)
        {
            if (double.IsNaN(price) || double.IsInfinity(price) || price != Math.Floor(price) || price < 1 || price > _limit)
                return false;
            var result = self.Obf_RuA<Obf_R.Obf_Ef>("SetPrice").Obf_HuA(self, price);
            if (result.Length == 0 || !(result[0] is bool accepted) || !accepted) return false;
            self.Obf_RuA<Obf_R.Obf_Ef>("RefreshPanelOnDataChange").Obf_HuA(self);
            return true;
        }
    }
}
