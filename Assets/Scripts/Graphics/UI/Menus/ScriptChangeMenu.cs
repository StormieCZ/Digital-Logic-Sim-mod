using DLS.Game;
using Seb.Helpers;
using Seb.Vis;
using Seb.Vis.UI;
using System;
using UnityEngine;

namespace DLS.Graphics
{
    public static class ScriptChangeMenu
    {
        // --- FIX ---
        // Renamed to be more accurate
        static SubChipInstance ScriptChip;
        
        static uint scriptID;

        // --- FIX ---
        // Changed the handle name to be unique to this menu
        static readonly UIHandle ID_ScriptInput = new("ScriptEdit_ScriptInput");
        static readonly Func<string, bool> integerInputValidator = ValidateScriptIDInput;

        public static void DrawMenu()
        {
            MenuHelper.DrawBackgroundOverlay();
            Draw.ID panelID = UI.ReservePanel();
            DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

            Vector2 pos = UI.Centre + Vector2.up * (UI.HalfHeight * 0.25f);

            using (UI.BeginBoundsScope(true))
            {
                UI.DrawText("JavaScript Script ID (e.g., 0 for 'script_0.js')", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentre, Color.white * 0.8f);

                InputFieldTheme inputFieldTheme = DrawSettings.ActiveUITheme.ChipNameInputField;
                inputFieldTheme.fontSize = DrawSettings.ActiveUITheme.FontSizeRegular;

                Vector2 size = new(5.6f, DrawSettings.SelectorWheelHeight);
                Vector2 inputPos = UI.PrevBounds.CentreBottom + Vector2.down * DrawSettings.VerticalButtonSpacing;

                InputFieldState state = UI.InputField(ID_ScriptInput, inputFieldTheme, inputPos, size, string.Empty, Anchor.CentreTop, 1, integerInputValidator, forceFocus: true);
                uint.TryParse(state.text, out scriptID);
                //Debug.Log($"{state.text} -> {scriptID}");

                MenuHelper.CancelConfirmResult result = MenuHelper.DrawCancelConfirmButtons(UI.GetCurrentBoundsScope().BottomLeft, UI.GetCurrentBoundsScope().Width, true);
                MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

                if (result == MenuHelper.CancelConfirmResult.Cancel)
                {
                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
                else if (result == MenuHelper.CancelConfirmResult.Confirm)
                {
                    // --- THIS IS THE FIX ---
                    // Add a null check before using ScriptChip,
                    // in case the menu is drawn before it's been set.
                    if (ScriptChip != null)
                    {
                        Project.ActiveProject.NotifyScriptChanged(ScriptChip, scriptID);
                    }
                    else
                    {
                        Debug.LogError("ScriptChangeMenu: ScriptChip was null. Could not save ID.");
                    }
                    // --- END FIX ---

                    UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
                }
            }
        }

        public static void OnMenuOpened()
        {
            // --- FIX ---
            // Get the chip and cast it
            ScriptChip = (SubChipInstance)ContextMenu.interactionContext;
            // The Script ID is stored at index 0
            scriptID = ScriptChip.InternalData[0];

            // Use the correct handle
            UI.GetInputFieldState(ID_ScriptInput).SetText(scriptID.ToString());
        }

        // --- FIX ---
        // Renamed function, but logic is fine for an ID
        public static bool ValidateScriptIDInput(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length > 7) return false; // An ID over 10 million seems unlikely
            if (s.Contains(" ")) return false;
            return int.TryParse(s, out _);
        }
    }
}