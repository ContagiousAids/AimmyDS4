using Gma.System.MouseKeyHook;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

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
            if (settingBindingId != null)
            {
                bindings[settingBindingId] = e.KeyCode.ToString();
                OnBindingSet?.Invoke(settingBindingId, e.KeyCode.ToString());
                settingBindingId = null;
            }
            else
            {
                foreach (var binding in bindings)
                {
                    if (binding.Value == e.KeyCode.ToString())
                    {
                        isHolding[binding.Key] = true;
                        OnBindingPressed?.Invoke(binding.Key);
                    }
                }
            }
        }

        private void GlobalHookMouseDown(object sender, MouseEventArgs e)
        {
            if (settingBindingId != null)
            {
                bindings[settingBindingId] = e.Button.ToString();
                OnBindingSet?.Invoke(settingBindingId, e.Button.ToString());
                settingBindingId = null;
            }
            else
            {
                foreach (var binding in bindings)
                {
                    if (binding.Value == e.Button.ToString())
                    {
                        isHolding[binding.Key] = true;
                        OnBindingPressed?.Invoke(binding.Key);
                    }
                }
            }
        }

        private void GlobalHookKeyUp(object sender, KeyEventArgs e)
        {
            foreach (var binding in bindings)
            {
                if (binding.Value == e.KeyCode.ToString())
                {
                    isHolding[binding.Key] = false;
                    OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        private void GlobalHookMouseUp(object sender, MouseEventArgs e)
        {
            foreach (var binding in bindings)
            {
                if (binding.Value == e.Button.ToString())
                {
                    isHolding[binding.Key] = false;
                    OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        private void HandleGamepadButtonState(string buttonName, bool pressed)
        {
            if (settingBindingId != null)
            {
                // When the user is setting a binding, store the xbox-style name (LB/RB)
                string storeName = buttonName switch
                {
                    "L1" => "LB",
                    "R1" => "RB",
                    _ => buttonName
                };

                bindings[settingBindingId] = storeName;
                isHolding[settingBindingId] = pressed;
                OnBindingSet?.Invoke(settingBindingId, storeName);
                settingBindingId = null;
                return;
            }

            foreach (var binding in bindings)
            {
                if (NormalizeGamepadButtonName(binding.Value) == NormalizeGamepadButtonName(buttonName))
                {
                    bool previous = isHolding.TryGetValue(binding.Key, out var prior) && prior;
                    isHolding[binding.Key] = pressed;
                    if (pressed && !previous)
                        OnBindingPressed?.Invoke(binding.Key);
                    else if (!pressed && previous)
                        OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        private static string NormalizeGamepadButtonName(string buttonName) => buttonName?.ToUpperInvariant() switch
        {
            "LB" => "L1",
            "L1" => "L1",
            "LEFTSHOULDER" => "L1",
            "RB" => "R1",
            "R1" => "R1",
            "RIGHTSHOULDER" => "R1",
            _ => buttonName ?? string.Empty
        };

        private void StartGamepadPoller()
        {
            Task.Run(async () =>
            {
                var lastState = new Dictionary<string, bool>
                {
                    ["L1"] = false,
                    ["R1"] = false
                };

                var xinput = new XInput();
                if (!xinput.IsAvailable)
                    return;

                while (true)
                {
                    try
                    {
                        var currentState = new Dictionary<string, bool>
                        {
                            ["L1"] = false,
                            ["R1"] = false
                        };

                        for (int i = 0; i < 4; i++)
                        {
                            if (xinput.GetState(i, out var state) == 0)
                            {
                                currentState["L1"] |= (state.Gamepad.wButtons & XInput.XINPUT_GAMEPAD_LEFT_SHOULDER) != 0;
                                currentState["R1"] |= (state.Gamepad.wButtons & XInput.XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;
                            }
                        }

                        foreach (var button in currentState.Keys)
                        {
                            if (lastState[button] != currentState[button])
                            {
                                HandleGamepadButtonState(button, currentState[button]);
                                lastState[button] = currentState[button];
                            }
                        }
                    }
                    catch
                    {
                    }

                    await Task.Delay(16);
                }
            });
        }

        private class XInput
        {
            public const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
            public const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;

            [StructLayout(LayoutKind.Sequential)]
            public struct XINPUT_GAMEPAD
            {
                public ushort wButtons;
                public byte bLeftTrigger;
                public byte bRightTrigger;
                public short sThumbLX;
                public short sThumbLY;
                public short sThumbRX;
                public short sThumbRY;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct XINPUT_STATE
            {
                public uint dwPacketNumber;
                public XINPUT_GAMEPAD Gamepad;
            }

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int XInputGetStateDelegate(int dwUserIndex, out XINPUT_STATE pState);

            private readonly XInputGetStateDelegate? _getState;

            public bool IsAvailable => _getState != null;

            public XInput()
            {
                _getState = LoadXInput();
            }

            public int GetState(int userIndex, out XINPUT_STATE state)
            {
                if (_getState != null)
                {
                    return _getState(userIndex, out state);
                }

                state = default;
                return -1;
            }

            private XInputGetStateDelegate? LoadXInput()
            {
                var handle = LoadLibrary("xinput1_4.dll");
                if (handle == IntPtr.Zero)
                    handle = LoadLibrary("xinput1_3.dll");

                if (handle == IntPtr.Zero)
                    return null;

                var proc = GetProcAddress(handle, "XInputGetState");
                if (proc == IntPtr.Zero)
                    return null;

                return Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(proc);
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        }

        public void StopListening()
        {
            if (_mEvents != null)
            {
                _mEvents.KeyDown -= GlobalHookKeyDown!;
                _mEvents.MouseDown -= GlobalHookMouseDown!;
                _mEvents.KeyUp -= GlobalHookKeyUp!;
                _mEvents.MouseUp -= GlobalHookMouseUp!;
                _mEvents.Dispose();
                _mEvents = null;
            }
        }
    }
}