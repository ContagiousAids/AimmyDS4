using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace Other
{
    internal class KeybindNameManager
    {
        public static Dictionary<string, string> KeybindNames = new()
        {
            { "D1", "1"}, { "D2", "2"}, { "D3", "3"}, { "D4", "4"}, { "D5", "5"},
            { "D6", "6"}, { "D7", "7"}, { "D8", "8"}, { "D9", "9"}, { "D0", "0"},
            { "OemMinus", "-"}, { "OemPlus", "+"}, { "Back", "Backspace"},
            { "Oem5", "Backslash (\\)"}, { "LMenu", "Left Alt"}, { "RMenu", "Right Alt"},
            { "RShiftKey", "Right Shift" }, { "LShiftKey", "Left Shift" },
            { "Oem4", "Left Bracket"}, { "OemOpenBrackets", "Left Bracket"},
            { "Oem6", "Right Bracket"}, { "Oem1", ";"}, { "Oem3", "`" },
            { "OemQuotes", "'"}, { "OemQuestion", "/"}, { "OemPeriod", "."},
            { "OemComma", ","}, { "Return", "Enter" },
        };

        public static string ConvertToRegularKey(string keyName)
        {
            if (string.IsNullOrEmpty(keyName) || keyName == "Unbound") 
                return "Unbound";

            if (string.Equals(keyName, "L1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(keyName, "LB", StringComparison.OrdinalIgnoreCase))
                return "LB";

            if (string.Equals(keyName, "R1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(keyName, "RB", StringComparison.OrdinalIgnoreCase))
                return "RB";

            try
            {
                KeyConverter kc = new();
                var keyObj = kc.ConvertFromString(keyName);
                if (keyObj is Key key)
                {
                    return KeybindNames.TryGetValue(key.ToString(), out var displayName) ? displayName : key.ToString();
                }
                return keyName;
            }
            catch
            {
                return keyName;
            }
        }
    }
}
