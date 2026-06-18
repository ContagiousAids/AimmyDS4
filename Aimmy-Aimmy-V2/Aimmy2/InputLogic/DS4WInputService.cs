using Aimmy2.Class;
using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace InputLogic
{
    internal static class DS4WInputService
    {
        private static UdpClient? _client;
        private static IPEndPoint? _endpoint;
        private static uint _packetCounter = 0;

        // Anti-recoil accumulator (persistent across calls — drifts stick downward)
        private static double _recoilAccumY = 0.0;

        static DS4WInputService()
        {
            try
            {
                _client = new UdpClient();
                // default endpoint; actual endpoint used will be resolved from settings at send time
                _endpoint = new IPEndPoint(IPAddress.Loopback, 26760);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DS4W] Init error: {ex.Message}");
            }
        }

        /// <summary>Main entry: apply controller deadzones, sensitivity, anti-recoil, then send to DS4Windows.</summary>
        public static void SendRightStick(double rawX, double rawY)
        {
            if (_client == null) return;

            try
            {
                var debugMsg = $"{DateTime.Now:HH:mm:ss} DEBUG -> SendRightStick rawX={rawX:F2} rawY={rawY:F2}";
                Console.WriteLine("[DS4W] " + debugMsg);
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    Dictionary.DS4Log.Add(debugMsg);
                    Dictionary.filelocationState["DS4 Last Send"] = debugMsg;
                }));
            }
            catch { }

            // Check enabled toggle
            try
            {
                if (Dictionary.toggleState.TryGetValue("DS4 UDP Enabled", out var enabledObj))
                {
                    if (!Convert.ToBoolean(enabledObj))
                    {
                        LogStatus($"{DateTime.Now:HH:mm:ss} SKIP -> DS4 UDP disabled", false);
                        return;
                    }
                }
            }
            catch { }

            // Resolve endpoint from settings (IP + Port)
            string ip = "127.0.0.1";
            int port = 26760;
            try
            {
                if (Dictionary.filelocationState.TryGetValue("DS4 UDP IP", out var ipVal))
                    ip = Convert.ToString(ipVal) ?? ip;

                if (Dictionary.sliderSettings.TryGetValue("DS4 UDP Port", out var portVal))
                    port = Convert.ToInt32(portVal);

                _endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
            }
            catch (Exception ex)
            {
                var errMsg = $"{DateTime.Now:HH:mm:ss} ERR -> Invalid DS4 UDP endpoint: {ex.Message}";
                Console.WriteLine($"[DS4W] Endpoint parse error: {ex.Message}");
                LogStatus(errMsg);
                return;
            }

            try
            {
                LogStatus($"{DateTime.Now:HH:mm:ss} SEND -> {_endpoint.Address}:{_endpoint.Port} rawX={rawX:F2} rawY={rawY:F2}", false);
                // Read user‑configured controller settings
                double sensX          = GetSlider("Controller Stick Sensitivity X", 0.50);
                double sensY          = GetSlider("Controller Stick Sensitivity Y", 0.50);
                double innerDead      = GetSlider("Controller Inner Deadzone", 0.05);
                double outerDead      = GetSlider("Controller Outer Deadzone", 0.95);
                double antiRecoil     = GetSlider("Controller Anti-Recoil Strength", 0.00);
                double minOutput      = GetSlider("Controller Minimum Output", 0.10);

                // 1. Apply inner deadzone
                (rawX, rawY) = ApplyDeadzone(rawX, rawY, innerDead);

                // 2. Apply sensitivity (separate X/Y)
                rawX *= sensX;
                rawY *= sensY;

                // 3. Clamp to outer deadzone, then re‑normalise to [-1, 1]
                (rawX, rawY) = ClampOuterDeadzone(rawX, rawY, outerDead);

                // 4. Minimum output: if stick is slightly moved, push it past a minimum threshold
                (rawX, rawY) = ApplyMinimumOutput(rawX, rawY, minOutput);

                // 5. Anti‑recoil: accumulate a small downward pull
                if (antiRecoil > 0.0)
                {
                    _recoilAccumY += antiRecoil * 0.002;  // small per‑call increment
                    _recoilAccumY = Math.Clamp(_recoilAccumY, 0.0, 0.3);
                    rawY += _recoilAccumY;
                }
                else
                {
                    _recoilAccumY = 0.0;
                }

                // Convert final -1..1 to 0‑255 byte range (128 = centre)
                byte stickX = (byte)Math.Clamp((rawX * 127.0) + 128.0, 0, 255);
                byte stickY = (byte)Math.Clamp((-rawY * 127.0) + 128.0, 0, 255);  // Y is inverted

                byte[] packet = BuildPacket(stickX, stickY);
                try
                {
                    Console.WriteLine($"[DS4W] Sending packet to {_endpoint.Address}:{_endpoint.Port} len={packet.Length} counter={_packetCounter}");
                    _client.Send(packet, packet.Length, _endpoint);
                    _packetCounter++;
                    var okMsg = $"{DateTime.Now:HH:mm:ss} OK -> {_endpoint.Address}:{_endpoint.Port} len={packet.Length} cnt={_packetCounter}";
                    LogStatus(okMsg);
                }
                catch (Exception ex)
                {
                    var errMsg = $"{DateTime.Now:HH:mm:ss} ERR -> {_endpoint.Address}:{_endpoint.Port} {ex.Message}";
                    Console.WriteLine($"[DS4W] UDP send failed: {ex.Message}");
                    LogStatus(errMsg);
                }
            }
            catch (Exception ex)
            {
                var errMsg = $"{DateTime.Now:HH:mm:ss} ERR -> Send error: {ex.Message}";
                Console.WriteLine($"[DS4W] Send error: {ex.Message}");
                LogStatus(errMsg);
            }
        }

        private static void LogStatus(string status, bool addToLog = true)
        {
            try
            {
                Dictionary.filelocationState["DS4 Last Send"] = status;
            }
            catch { }

            if (!addToLog)
                return;

            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Dictionary.DS4Log.Add(status)));
            }
            catch { }
        }

        /// <summary>Reset the anti‑recoil drift (call when aim key is released).</summary>
        public static void ResetAntiRecoil()
        {
            _recoilAccumY = 0.0;
        }

        // ---- helper maths ----

        private static (double x, double y) ApplyDeadzone(double x, double y, double inner)
        {
            double mag = Math.Sqrt(x * x + y * y);
            if (mag <= inner)
                return (0.0, 0.0);

            // Scale the usable range from [inner, 1.0] → [0.0, 1.0]
            double scale = (mag - inner) / (1.0 - inner);
            double ratio = scale / mag;
            return (x * ratio, y * ratio);
        }

        private static (double x, double y) ClampOuterDeadzone(double x, double y, double outer)
        {
            double mag = Math.Sqrt(x * x + y * y);
            if (mag <= outer)
                return (x, y);

            double ratio = outer / mag;
            return (x * ratio, y * ratio);
        }

        private static (double x, double y) ApplyMinimumOutput(double x, double y, double minOut)
        {
            double mag = Math.Sqrt(x * x + y * y);
            if (mag <= 0.0 || mag >= minOut)
                return (x, y);

            double ratio = minOut / mag;
            return (x * ratio, y * ratio);
        }

        private static double GetSlider(string key, double fallback)
        {
            try
            {
                if (Dictionary.sliderSettings.TryGetValue(key, out var val))
                    return Convert.ToDouble(val);
            }
            catch { }
            return fallback;
        }

        private static bool GetToggle(string key, bool fallback)
        {
            try
            {
                if (Dictionary.toggleState.TryGetValue(key, out var val))
                    return Convert.ToBoolean(val);
            }
            catch { }
            return fallback;
        }

        private static uint GetClientId()
        {
            try
            {
                if (Dictionary.sliderSettings.TryGetValue("DS4 UDP Client ID", out var val))
                    return Convert.ToUInt32(val);
            }
            catch { }
            return 1u;
        }

        // ---- packet building ----

        private static byte[] BuildPacket(byte rightX, byte rightY)
        {
            bool useCrc = GetToggle("DS4 UDP Use CRC", true);
            uint clientId = GetClientId();

            using var ms = new System.IO.MemoryStream();
            using var bw = new System.IO.BinaryWriter(ms);

            // DSUS/DSUC protocol header
            bw.Write(new byte[] { 0x44, 0x53, 0x55, 0x43 }); // "DSUC" magic
            bw.Write((ushort)1001);  // Protocol version
            bw.Write((ushort)76);    // Packet length

            // CRC32 placeholder (filled after we have the full packet)
            bw.Write((uint)0);

            // Client ID
            bw.Write(clientId);

            // Message type: controller data (0x100002)
            bw.Write((uint)0x100002);

            // Slot info
            bw.Write((byte)0);   // Pad ID
            bw.Write((byte)2);   // Pad state (connected)

            // Buttons (all unpressed)
            bw.Write((ushort)0); // Buttons 1+2
            bw.Write((byte)0);   // PS button
            bw.Write((byte)0);   // Touch button

            // Left stick (centered)
            bw.Write((byte)128);
            bw.Write((byte)128);

            // Right stick (our aim values)
            bw.Write(rightX);
            bw.Write(rightY);

            // Analog face buttons (all 0)
            bw.Write((byte)0); // dpad left
            bw.Write((byte)0); // dpad down
            bw.Write((byte)0); // dpad right
            bw.Write((byte)0); // dpad up
            bw.Write((byte)0); // square
            bw.Write((byte)0); // cross
            bw.Write((byte)0); // circle
            bw.Write((byte)0); // triangle
            bw.Write((byte)0); // R1
            bw.Write((byte)0); // L1
            bw.Write((byte)0); // R2
            bw.Write((byte)0); // L2

            // Touch data (unused)
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);

            // Timestamp
            bw.Write((ulong)(_packetCounter * 1000));

            // Accelerometer (flat/still)
            bw.Write((float)0);
            bw.Write((float)0);
            bw.Write((float)1); // gravity Z
            bw.Write((float)0);
            bw.Write((float)0);
            bw.Write((float)0);

            var packet = ms.ToArray();

            if (useCrc)
            {
                // Compute CRC32 over entire packet with CRC field considered as 0 (common DS4Windows behavior)
                // CRC field offset = 8 bytes ("DSUC"=4 + ver=2 + len=2) then CRC uint32 starts.
                const int crcOffset = 8;
                const int crcSize = 4;

                var crcInput = new byte[packet.Length];
                Buffer.BlockCopy(packet, 0, crcInput, 0, packet.Length);

                for (int i = 0; i < crcSize; i++)
                    crcInput[crcOffset + i] = 0;

                uint crc = Crc32.Compute(crcInput);

                // Write CRC little-endian
                packet[crcOffset + 0] = (byte)(crc & 0xFF);
                packet[crcOffset + 1] = (byte)((crc >> 8) & 0xFF);
                packet[crcOffset + 2] = (byte)((crc >> 16) & 0xFF);
                packet[crcOffset + 3] = (byte)((crc >> 24) & 0xFF);
            }

            return packet;
        }
    }
}
