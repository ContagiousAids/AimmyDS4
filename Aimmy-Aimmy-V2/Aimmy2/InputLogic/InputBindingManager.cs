using Gma.System.MouseKeyHook;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

// Use aliases to prevent ambiguity with WPF
using WinForms = System.Windows.Forms;
using KeyEventArgs = System.Windows.Forms.KeyEventArgs;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;

namespace InputLogic
{
    internal class InputBindingManager
    {
        private IKeyboardMouseEvents? _mEvents;
        private readonly Dictionary<string, string> bindings = new();
        private static readonly Dictionary<string, bool> isHolding = new();
        private string? settingBindingId = null;
        private bool _gamepadPollerStarted;

        public event Action<string, string>? OnBindingSet;
        public event Action<string>? OnBindingPressed;
        public event Action<string>? OnBindingReleased;

        public static bool IsHoldingBinding(string bindingId) => isHolding.TryGetValue(bindingId, out bool holding) && holding;

        public void SetupDefault(string bindingId, string keyCode)
        {
            bindings[bindingId] = keyCode;
            isHolding[bindingId] = false;
            OnBindingSet?.Invoke(bindingId, keyCode);
            EnsureHookEvents();
        }

        public void StartListeningForBinding(string bindingId)
        {
            settingBindingId = bindingId;
            EnsureHookEvents();
        }

        private void EnsureHookEvents()
        {
            if (_mEvents == null)
            {
                _mEvents = Hook.GlobalEvents();
                _mEvents.KeyDown += GlobalHookKeyDown!;
                _mEvents.MouseDown += GlobalHookMouseDown!;
                _mEvents.KeyUp += GlobalHookKeyUp!;
                _mEvents.MouseUp += GlobalHookMouseUp!;
            }

            if (!_gamepadPollerStarted)
            {
                _gamepadPollerStarted = true;
                StartGamepadPoller();
            }
        }

        private void GlobalHookKeyDown(object sender, KeyEventArgs e)
        {
            if (settingBindingId != null) {
                bindings[settingBindingId] = e.KeyCode.ToString();
                OnBindingSet?.Invoke(settingBindingId, e.KeyCode.ToString());
                settingBindingId = null;
            } else {
                foreach (var binding in bindings) {
                    if (binding.Value == e.KeyCode.ToString()) {
                        isHolding[binding.Key] = true;
                        OnBindingPressed?.Invoke(binding.Key);
                    }
                }
            }
        }

        private void GlobalHookMouseDown(object sender, MouseEventArgs e)
        {
            if (settingBindingId != null) {
                bindings[settingBindingId] = e.Button.ToString();
                OnBindingSet?.Invoke(settingBindingId, e.Button.ToString());
                settingBindingId = null;
            } else {
                foreach (var binding in bindings) {
                    if (binding.Value == e.Button.ToString()) {
                        isHolding[binding.Key] = true;
                        OnBindingPressed?.Invoke(binding.Key);
                    }
                }
            }
        }

        private void GlobalHookKeyUp(object sender, KeyEventArgs e)
        {
            foreach (var binding in bindings) {
                if (binding.Value == e.KeyCode.ToString()) {
                    isHolding[binding.Key] = false;
                    OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        private void GlobalHookMouseUp(object sender, MouseEventArgs e)
        {
            foreach (var binding in bindings) {
                if (binding.Value == e.Button.ToString()) {
                    isHolding[binding.Key] = false;
                    OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        private void HandleGamepadButtonState(string buttonName, bool pressed)
        {
            if (settingBindingId != null && pressed)
            {
                bindings[settingBindingId] = buttonName;
                OnBindingSet?.Invoke(settingBindingId, buttonName);
                settingBindingId = null;
                return;
            }

            foreach (var binding in bindings)
            {
                if (binding.Value == buttonName)
                {
                    bool previous = isHolding.TryGetValue(binding.Key, out var prior) && prior;
                    isHolding[binding.Key] = pressed;
                    if (pressed && !previous) OnBindingPressed?.Invoke(binding.Key);
                    else if (!pressed && previous) OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        private void StartGamepadPoller()
        {
            Task.Run(async () =>
            {
                var xinput = new XInput();
                if (!xinput.IsAvailable) return;

                var lastStates = new Dictionary<string, bool>();
                string[] buttonNames = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS", "Start", "Back", "Up", "Down", "Left", "Right" };
                foreach (var btn in buttonNames) lastStates[btn] = false;

                while (true)
                {
                    try
                    {
                        var currentStates = new Dictionary<string, bool>();
                        foreach (var btn in buttonNames) currentStates[btn] = false;

                        for (int i = 0; i < 4; i++)
                        {
                            if (xinput.GetState(i, out var state) == 0)
                            {
                                var b = state.Gamepad.wButtons;
                                currentStates["A"] |= (b & 0x1000) != 0;
                                currentStates["B"] |= (b & 0x2000) != 0;
                                currentStates["X"] |= (b & 0x4000) != 0;
                                currentStates["Y"] |= (b & 0x8000) != 0;
                                currentStates["LB"] |= (b & 0x0100) != 0;
                                currentStates["RB"] |= (b & 0x0200) != 0;
                                currentStates["LS"] |= (b & 0x0040) != 0;
                                currentStates["RS"] |= (b & 0x0080) != 0;
                                currentStates["Start"] |= (b & 0x0010) != 0;
                                currentStates["Back"] |= (b & 0x0020) != 0;
                                currentStates["Up"] |= (b & 0x0001) != 0;
                                currentStates["Down"] |= (b & 0x0002) != 0;
                                currentStates["Left"] |= (b & 0x0004) != 0;
                                currentStates["Right"] |= (b & 0x0008) != 0;
                                currentStates["LT"] |= state.Gamepad.bLeftTrigger > 50;
                                currentStates["RT"] |= state.Gamepad.bRightTrigger > 50;
                            }
                        }

                        foreach (var btn in buttonNames)
                        {
                            if (currentStates[btn] != lastStates[btn])
                            {
                                HandleGamepadButtonState(btn, currentStates[btn]);
                                lastStates[btn] = currentStates[btn];
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(16);
                }
            });
        }

        private class XInput
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct XINPUT_GAMEPAD {
                public ushort wButtons;
                public byte bLeftTrigger;
                public byte bRightTrigger;
                public short sThumbLX; public short sThumbLY;
                public short sThumbRX; public short sThumbRY;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct XINPUT_STATE {
                public uint dwPacketNumber;
                public XINPUT_GAMEPAD Gamepad;
            }

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int XInputGetStateDelegate(int dwUserIndex, out XINPUT_STATE pState);
            private readonly XInputGetStateDelegate? _getState;
            public bool IsAvailable => _getState != null;

            public XInput() {
                IntPtr handle = LoadLibrary("xinput1_4.dll");
                if (handle == IntPtr.Zero) handle = LoadLibrary("xinput1_3.dll");
                if (handle != IntPtr.Zero) {
                    IntPtr proc = GetProcAddress(handle, "XInputGetState");
                    if (proc != IntPtr.Zero) _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(proc);
                }
            }

            public int GetState(int userIndex, out XINPUT_STATE state) {
                if (_getState != null) return _getState(userIndex, out state);
                state = default; return -1;
            }

            [DllImport("kernel32.dll")] private static extern IntPtr LoadLibrary(string lpFileName);
            [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        }
    }
}
