using DLS.Game;
using Seb.Helpers;
using Seb.Vis;
using Seb.Vis.UI;
using System;
using UnityEngine;

namespace DLS.Graphics
{
	public static class PortEditMenu
	{
		static SubChipInstance WebsocketChip;
		static uint port;

		static readonly UIHandle ID_PortInput = new("PortEdit_Port");
		static readonly Func<string, bool> integerInputValidator = ValidatePortInput;

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			Draw.ID panelID = UI.ReservePanel();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

			Vector2 pos = UI.Centre + Vector2.up * (UI.HalfHeight * 0.25f);

			using (UI.BeginBoundsScope(true))
			{
				UI.DrawText("Websocket Port", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentre, Color.white * 0.8f);

				InputFieldTheme inputFieldTheme = DrawSettings.ActiveUITheme.ChipNameInputField;
				inputFieldTheme.fontSize = DrawSettings.ActiveUITheme.FontSizeRegular;

				Vector2 size = new(5.6f, DrawSettings.SelectorWheelHeight);
				Vector2 inputPos = UI.PrevBounds.CentreBottom + Vector2.down * DrawSettings.VerticalButtonSpacing;
				InputFieldState state = UI.InputField(ID_PortInput, inputFieldTheme, inputPos, size, string.Empty, Anchor.CentreTop, 1, integerInputValidator, forceFocus: true);
				uint.TryParse(state.text, out port);

				MenuHelper.CancelConfirmResult result = MenuHelper.DrawCancelConfirmButtons(UI.GetCurrentBoundsScope().BottomLeft, UI.GetCurrentBoundsScope().Width, true);
				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

				if (result == MenuHelper.CancelConfirmResult.Cancel)
				{
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
				else if (result == MenuHelper.CancelConfirmResult.Confirm)
				{
					Project.ActiveProject.NotifyPortChanged(WebsocketChip, port);
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
			}
		}

		public static void OnMenuOpened()
		{
			WebsocketChip = (SubChipInstance)ContextMenu.interactionContext;
			port = WebsocketChip.InternalData[1];
			UI.GetInputFieldState(ID_PortInput).SetText(port.ToString());
		}

		public static bool ValidatePortInput(string s)
		{
			if (string.IsNullOrEmpty(s)) return true;
            if (s.Length > 7 || s.Length < 0) return false;
			if (s.Contains(" ")) return false;
			return int.TryParse(s, out _);
		}
	}
}