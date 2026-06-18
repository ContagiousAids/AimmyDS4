/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ComponentModel;
using System.Threading;
using System.Runtime.InteropServices;

namespace DS4Windows
{
    public enum DsState : byte
    {
        [Description("Disconnected")]
        Disconnected = 0x00,
        [Description("Reserved")]
        Reserved = 0x01,
        [Description("Connected")]
        Connected = 0x02
    };

    public enum DsConnection : byte
    {
        [Description("None")]
        None = 0x00,
        [Description("Usb")]
        Usb = 0x01,
        [Description("Bluetooth")]
        Bluetooth = 0x02
    };

    public enum DsModel : byte
    {
        [Description("None")]
        None = 0,
        [Description("DualShock 3")]
        DS3 = 1,
        [Description("DualShock 4")]
        DS4 = 2,
        [Description("Generic Gamepad")]
        Generic = 3
    }

    public enum DsBattery : byte
    {
        None = 0x00,
        Dying = 0x01,
        Low = 0x02,
        Medium = 0x03,
        High = 0x04,
        Full = 0x05,
        Charging = 0xEE,
        Charged = 0xEF
    };

    public struct DualShockPadMeta
    {
        public byte PadId;
        public DsState PadState;
        public DsConnection ConnectionType;
        public DsModel Model;
        public PhysicalAddress PadMacAddress;
        public DsBattery BatteryStatus;
        public bool IsActive;
    }

    [StructLayout(LayoutKind.Explicit, Size = 100)]
    unsafe struct PadDataRspPacket
    {
        // Header section
        [FieldOffset(0)]
        public fixed byte initCode[4];
        [FieldOffset(4)]
        public ushort protocolVersion;
        [FieldOffset(6)]
        public ushort messageLen;
        [FieldOffset(8)]
        public int crc;
        [FieldOffset(12)]
        public uint serverId;
        [FieldOffset(16)]
        public uint messageType;

        // Pad meta section
        [FieldOffset(20)]
        public byte padId;
        [FieldOffset(21)]
        public byte padState;
        [FieldOffset(22)]
        public byte model;
        [FieldOffset(23)]
        public byte connectionType;
        [FieldOffset(24)]
        public fixed byte address[6];
        [FieldOffset(30)]
        public byte batteryStatus;
        [FieldOffset(31)]
        public byte isActive;
        [FieldOffset(32)]
        public uint packetCounter;

        // Primary controls
        [FieldOffset(36)]
        public byte buttons1;
        [FieldOffset(37)]
        public byte buttons2;
        [FieldOffset(38)]
        public byte psButton;
        [FieldOffset(39)]
        public byte touchButton;
        [FieldOffset(40)]
        public byte lx;
        [FieldOffset(41)]
        public byte ly;
        [FieldOffset(42)]
        public byte rx;
        [FieldOffset(43)]
        public byte ry;
        [FieldOffset(44)]
        public byte dpadLeft;
        [FieldOffset(45)]
        public byte dpadDown;
        [FieldOffset(46)]
        public byte dpadRight;
        [FieldOffset(47)]
        public byte dpadUp;
        [FieldOffset(48)]
        public byte square;
        [FieldOffset(49)]
        public byte cross;
        [FieldOffset(50)]
        public byte circle;
        [FieldOffset(51)]
        public byte triangle;
        [FieldOffset(52)]
        public byte r1;
        [FieldOffset(53)]
        public byte l1;
        [FieldOffset(54)]
        public byte r2;
        [FieldOffset(55)]
        public byte l2;

        // Touch 1
        [FieldOffset(56)]
        public byte touch1Active;
        [FieldOffset(57)]
        public byte touch1PacketId;
        [FieldOffset(58)]
        public ushort touch1X;
        [FieldOffset(60)]
        public ushort touch1Y;

        // Touch 2
        [FieldOffset(62)]
        public byte touch2Active;
        [FieldOffset(63)]
        public byte touch2PacketId;
        [FieldOffset(64)]
        public ushort touch2X;
        [FieldOffset(66)]
        public ushort touch2Y;

        // Accel
        [FieldOffset(68)]
        public ulong totalMicroSec;
        [FieldOffset(76)]
        public float accelXG;
        [FieldOffset(80)]
        public float accelYG;
        [FieldOffset(84)]
        public float accelZG;

        // Gyro
        [FieldOffset(88)]
        public float angVelPitch;
        [FieldOffset(92)]
        public float angVelYaw;
        [FieldOffset(96)]
        public float angVelRoll;
    }

    class UdpServer
    {
        public const int NUMBER_SLOTS = 4;
        private Socket udpSock;
        private uint serverId;
        private bool running;
        private byte[] recvBuffer = new byte[1024];
        private byte[][] dataBuffers;
        private int listInd = 0;
        private ReaderWriterLockSlim poolLock = new ReaderWriterLockSlim();
        private SemaphoreSlim _pool;
        private const int ARG_BUFFER_LEN = 80;

        // -----------------------------------------------------------------------
        // UDP INPUT RECEIVE: second socket that listens for incoming controller
        // state packets on a separate port (default 26761).
        //
        // Packet format (16 bytes minimum, magic header "DS4I"):
        //   [0..3]   magic: 'D','S','4','I'
        //   [4]      buttons1  (DpadLeft|DpadDown|DpadRight|DpadUp|Options|R3|L3|Share)
        //   [5]      buttons2  (Square|Cross|Circle|Triangle|R1|L1|R2Btn|L2Btn)
        //   [6]      psButton  (1 = pressed)
        //   [7]      touchButton (1 = pressed)
        //   [8]      LX  (0-255, 128 = center)
        //   [9]      LY  (0-255, 128 = center, NOT pre-inverted)
        //   [10]     RX  (0-255, 128 = center)
        //   [11]     RY  (0-255, 128 = center, NOT pre-inverted)
        //   [12]     L2  (0-255 analog)
        //   [13]     R2  (0-255 analog)
        //   [14]     padIndex (0-3, which virtual pad slot to inject into)
        //   [15]     reserved / padding
        //
        // To disable UDP input receive entirely, set UdpInputEnabled = false
        // before calling Start(), or just don't send anything to port 26761.
        // -----------------------------------------------------------------------
        public const int DEFAULT_INPUT_PORT = 26761;
        public bool UdpInputEnabled { get; set; } = true;

        private Socket udpInputSock;
        private byte[] inputRecvBuffer = new byte[256];
        private Thread inputReceiveThread;

        // One injected state per pad slot (matches NUMBER_SLOTS = 4)
        private DS4State[] udpInjectedStates;
        private object[] udpInjectedStateLocks;
        private DateTime[] udpInjectedStateTimes;
        private DateTime lastUdpInputLogTime = DateTime.MinValue;

        // Public accessor so DS4Device / ControlService can read the injected state
        public DS4State GetUdpInjectedState(int padIndex)
        {
            if (padIndex < 0 || padIndex >= NUMBER_SLOTS) return null;
            lock (udpInjectedStateLocks[padIndex])
            {
                if ((DateTime.UtcNow - udpInjectedStateTimes[padIndex]).TotalMilliseconds > 250)
                    return null;

                // Return a shallow copy so the caller isn't racing on the same object
                return udpInjectedStates[padIndex]?.Clone() as DS4State;
            }
        }

        // Fired whenever a new input packet arrives – wire this up in ControlService
        // if you want event-driven injection rather than polling GetUdpInjectedState().
        public event Action<int, DS4State> UdpInputReceived;
        // -----------------------------------------------------------------------

        public delegate void GetPadDetail(int padIdx, ref DualShockPadMeta meta);

        private GetPadDetail portInfoGet;

        public UdpServer(GetPadDetail getPadDetailDel)
        {
            portInfoGet = getPadDetailDel;
            _pool = new SemaphoreSlim(ARG_BUFFER_LEN);
            dataBuffers = new byte[ARG_BUFFER_LEN][];
            for (int num = 0; num < ARG_BUFFER_LEN; num++)
            {
                SocketAsyncEventArgs args = new SocketAsyncEventArgs();
                args.Completed += SocketEvent_AsyncCompleted;
                dataBuffers[num] = new byte[100];
            }

            // Initialise per-slot injected states
            udpInjectedStates = new DS4State[NUMBER_SLOTS];
            udpInjectedStateLocks = new object[NUMBER_SLOTS];
            udpInjectedStateTimes = new DateTime[NUMBER_SLOTS];
            for (int i = 0; i < NUMBER_SLOTS; i++)
            {
                udpInjectedStates[i] = new DS4State();
                udpInjectedStateLocks[i] = new object();
                udpInjectedStateTimes[i] = DateTime.MinValue;
            }
        }

        private void SocketEvent_AsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            _pool.Release();
            e.Dispose();
        }

        private void CompletedSynchronousSocketEvent(SocketAsyncEventArgs args)
        {
            _pool.Release();
            args.Dispose();
        }

        enum MessageType
        {
            DSUC_VersionReq = 0x100000,
            DSUS_VersionRsp = 0x100000,
            DSUC_ListPorts = 0x100001,
            DSUS_PortInfo = 0x100001,
            DSUC_PadDataReq = 0x100002,
            DSUS_PadDataRsp = 0x100002,
        };

        private const ushort MaxProtocolVersion = 1001;
        public const int DATA_RSP_PACKET_LEN = 100;

        class ClientRequestTimes
        {
            DateTime allPads;
            DateTime[] padIds;
            Dictionary<PhysicalAddress, DateTime> padMacs;

            public DateTime AllPadsTime { get { return allPads; } }
            public DateTime[] PadIdsTime { get { return padIds; } }
            public Dictionary<PhysicalAddress, DateTime> PadMacsTime { get { return padMacs; } }

            public ClientRequestTimes()
            {
                allPads = DateTime.MinValue;
                padIds = new DateTime[4];

                for (int i = 0; i < padIds.Length; i++)
                    padIds[i] = DateTime.MinValue;

                padMacs = new Dictionary<PhysicalAddress, DateTime>();
            }

            public void RequestPadInfo(byte regFlags, byte idToReg, PhysicalAddress macToReg)
            {
                if (regFlags == 0)
                    allPads = DateTime.UtcNow;
                else
                {
                    if ((regFlags & 0x01) != 0) //id valid
                    {
                        if (idToReg < padIds.Length)
                            padIds[idToReg] = DateTime.UtcNow;
                    }
                    if ((regFlags & 0x02) != 0) //mac valid
                    {
                        padMacs[macToReg] = DateTime.UtcNow;
                    }
                }
            }
        }

        private Dictionary<IPEndPoint, ClientRequestTimes> clients = new Dictionary<IPEndPoint, ClientRequestTimes>();

        private int BeginPacket(byte[] packetBuf, ushort reqProtocolVersion = MaxProtocolVersion)
        {
            int currIdx = 0;
            packetBuf[currIdx++] = (byte)'D';
            packetBuf[currIdx++] = (byte)'S';
            packetBuf[currIdx++] = (byte)'U';
            packetBuf[currIdx++] = (byte)'S';

            Array.Copy(BitConverter.GetBytes((ushort)reqProtocolVersion), 0, packetBuf, currIdx, 2);
            currIdx += 2;

            Array.Copy(BitConverter.GetBytes((ushort)packetBuf.Length - 16), 0, packetBuf, currIdx, 2);
            currIdx += 2;

            Array.Clear(packetBuf, currIdx, 4); //place for crc
            currIdx += 4;

            Array.Copy(BitConverter.GetBytes((uint)serverId), 0, packetBuf, currIdx, 4);
            currIdx += 4;

            return currIdx;
        }

        private unsafe void BeginDataRspPacket(ref PadDataRspPacket currentRsp, ushort reqProtocolVersion = MaxProtocolVersion)
        {
            const int outputPacketLen = 100;

            currentRsp.initCode[0] = (byte)'D';
            currentRsp.initCode[1] = (byte)'S';
            currentRsp.initCode[2] = (byte)'U';
            currentRsp.initCode[3] = (byte)'S';

            currentRsp.protocolVersion = reqProtocolVersion;
            currentRsp.messageLen = (ushort)outputPacketLen - 16;

            currentRsp.crc = 0;
            currentRsp.serverId = serverId;
        }

        private void FinishPacket(byte[] packetBuf)
        {
            Array.Clear(packetBuf, 8, 4);

            uint seed = Crc32Algorithm.DefaultSeed;
            uint crcCalc = ~Crc32Algorithm.CalculateBasicHash(ref seed, ref packetBuf, 0, packetBuf.Length);
            Array.Copy(BitConverter.GetBytes((uint)crcCalc), 0, packetBuf, 8, 4);
        }

        private unsafe void FinishDataRspPacket(ref PadDataRspPacket currentRsp, byte[] packetBuf)
        {
            currentRsp.crc = 0;
            CopyBytes(ref currentRsp, packetBuf, DATA_RSP_PACKET_LEN);

            uint seed = Crc32Algorithm.DefaultSeed;
            uint crcCalc = ~Crc32Algorithm.CalculateBasicHash(ref seed, ref packetBuf, 0, packetBuf.Length);
            Array.Copy(BitConverter.GetBytes((uint)crcCalc), 0, packetBuf, 8, 4);
        }

        private void SendPacket(IPEndPoint clientEP, byte[] usefulData, ushort reqProtocolVersion = MaxProtocolVersion)
        {
            byte[] packetData = new byte[usefulData.Length + 16];
            int currIdx = BeginPacket(packetData, reqProtocolVersion);
            Array.Copy(usefulData, 0, packetData, currIdx, usefulData.Length);
            FinishPacket(packetData);

            int temp = 0;
            poolLock.EnterWriteLock();
            temp = listInd;
            listInd = ++listInd % ARG_BUFFER_LEN;
            SocketAsyncEventArgs args = new SocketAsyncEventArgs()
            {
                RemoteEndPoint = clientEP,
            };
            args.SetBuffer(dataBuffers[temp], 0, 100);
            args.Completed += SocketEvent_AsyncCompleted;
            poolLock.ExitWriteLock();

            _pool.Wait();
            Array.Copy(packetData, args.Buffer, packetData.Length);
            bool sentAsync = false;
            try {
                sentAsync = udpSock.SendToAsync(args);
            }
            catch (Exception /*e*/) { }
            finally
            {
                if (!sentAsync) CompletedSynchronousSocketEvent(args);
            }
        }

        private void ProcessIncoming(byte[] localMsg, IPEndPoint clientEP)
        {
            try
            {
                if (TryParseAimmyDsucInputPacket(localMsg, localMsg.Length))
                    return;

                int currIdx = 0;
                if (localMsg[0] != 'D' || localMsg[1] != 'S' || localMsg[2] != 'U' || localMsg[3] != 'C')
                    return;
                else
                    currIdx += 4;

                uint protocolVer = BitConverter.ToUInt16(localMsg, currIdx);
                currIdx += 2;

                if (protocolVer > MaxProtocolVersion)
                    return;

                uint packetSize = BitConverter.ToUInt16(localMsg, currIdx);
                currIdx += 2;

                if (packetSize < 0)
                    return;

                packetSize += 16; //size of header
                if (packetSize > localMsg.Length)
                    return;
                else if (packetSize < localMsg.Length)
                {
                    byte[] newMsg = new byte[packetSize];
                    Array.Copy(localMsg, newMsg, packetSize);
                    localMsg = newMsg;
                }

                uint crcValue = BitConverter.ToUInt32(localMsg, currIdx);
                //zero out the crc32 in the packet once we got it since that's whats needed for calculation
                localMsg[currIdx++] = 0;
                localMsg[currIdx++] = 0;
                localMsg[currIdx++] = 0;
                localMsg[currIdx++] = 0;

                uint crcCalc = Crc32Algorithm.Compute(localMsg);
                if (crcValue != crcCalc)
                    return;

                uint clientId = BitConverter.ToUInt32(localMsg, currIdx);
                currIdx += 4;

                uint messageType = BitConverter.ToUInt32(localMsg, currIdx);
                currIdx += 4;

                if (messageType == (uint)MessageType.DSUC_VersionReq)
                {
                    byte[] outputData = new byte[8];
                    int outIdx = 0;
                    Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_VersionRsp), 0, outputData, outIdx, 4);
                    outIdx += 4;
                    Array.Copy(BitConverter.GetBytes((ushort)MaxProtocolVersion), 0, outputData, outIdx, 2);
                    outIdx += 2;
                    outputData[outIdx++] = 0;
                    outputData[outIdx++] = 0;

                    SendPacket(clientEP, outputData, 1001);
                }
                else if (messageType == (uint)MessageType.DSUC_ListPorts)
                {
                    int numPadRequests = BitConverter.ToInt32(localMsg, currIdx);
                    currIdx += 4;
                    if (numPadRequests < 0 || numPadRequests > NUMBER_SLOTS)
                        return;

                    int requestsIdx = currIdx;
                    for (int i = 0; i < numPadRequests; i++)
                    {
                        byte currRequest = localMsg[requestsIdx + i];
                        if (currRequest >= NUMBER_SLOTS)
                            return;
                    }

                    byte[] outputData = new byte[16];
                    for (byte i = 0; i < numPadRequests; i++)
                    {
                        byte currRequest = localMsg[requestsIdx + i];
                        DualShockPadMeta padData = new DualShockPadMeta();
                        portInfoGet(currRequest, ref padData);

                        int outIdx = 0;
                        Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_PortInfo), 0, outputData, outIdx, 4);
                        outIdx += 4;

                        outputData[outIdx++] = (byte)padData.PadId;
                        outputData[outIdx++] = (byte)padData.PadState;
                        outputData[outIdx++] = (byte)padData.Model;
                        outputData[outIdx++] = (byte)padData.ConnectionType;

                        byte[] addressBytes = null;
                        if (padData.PadMacAddress != null)
                            addressBytes = padData.PadMacAddress.GetAddressBytes();

                        if (addressBytes != null && addressBytes.Length == 6)
                        {
                            outputData[outIdx++] = addressBytes[0];
                            outputData[outIdx++] = addressBytes[1];
                            outputData[outIdx++] = addressBytes[2];
                            outputData[outIdx++] = addressBytes[3];
                            outputData[outIdx++] = addressBytes[4];
                            outputData[outIdx++] = addressBytes[5];
                        }
                        else
                        {
                            outputData[outIdx++] = 0;
                            outputData[outIdx++] = 0;
                            outputData[outIdx++] = 0;
                            outputData[outIdx++] = 0;
                            outputData[outIdx++] = 0;
                            outputData[outIdx++] = 0;
                        }

                        outputData[outIdx++] = (byte)padData.BatteryStatus;
                        outputData[outIdx++] = 0;

                        SendPacket(clientEP, outputData, 1001);
                    }
                }
                else if (messageType == (uint)MessageType.DSUC_PadDataReq)
                {
                    byte regFlags = localMsg[currIdx++];
                    byte idToReg = localMsg[currIdx++];
                    PhysicalAddress macToReg = null;
                    {
                        byte[] macBytes = new byte[6];
                        Array.Copy(localMsg, currIdx, macBytes, 0, macBytes.Length);
                        currIdx += macBytes.Length;
                        macToReg = new PhysicalAddress(macBytes);
                    }

                    lock (clients)
                    {
                        if (clients.ContainsKey(clientEP))
                            clients[clientEP].RequestPadInfo(regFlags, idToReg, macToReg);
                        else
                        {
                            var clientTimes = new ClientRequestTimes();
                            clientTimes.RequestPadInfo(regFlags, idToReg, macToReg);
                            clients[clientEP] = clientTimes;
                        }
                    }
                }
            }
            catch (Exception /*e*/) { }
        }

        private void ReceiveCallback(IAsyncResult iar)
        {
            byte[] localMsg = null;
            EndPoint clientEP = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                Socket recvSock = (Socket)iar.AsyncState;
                int msgLen = recvSock.EndReceiveFrom(iar, ref clientEP);

                localMsg = new byte[msgLen];
                Array.Copy(recvBuffer, localMsg, msgLen);
            }
            catch (SocketException)
            {
                if (running)
                {
                    ResetUDPConn();
                }
            }
            catch (Exception /*e*/) { }

            StartReceive();

            if (localMsg != null)
                ProcessIncoming(localMsg, (IPEndPoint)clientEP);
        }

        private void StartReceive()
        {
            try
            {
                if (running)
                {
                    EndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);
                    udpSock.BeginReceiveFrom(recvBuffer, 0, recvBuffer.Length, SocketFlags.None, ref newClientEP, ReceiveCallback, udpSock);
                }
            }
            catch (SocketException /*ex*/)
            {
                if (running)
                {
                    ResetUDPConn();
                    StartReceive();
                }
            }
        }

        private void ResetUDPConn()
        {
            uint IOC_IN = 0x80000000;
            uint IOC_VENDOR = 0x18000000;
            uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
            udpSock.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
        }

        // -----------------------------------------------------------------------
        // UDP INPUT RECEIVE IMPLEMENTATION
        // -----------------------------------------------------------------------

        /// <summary>
        /// Parses a UDP input packet and writes its values into the matching
        /// udpInjectedStates slot.  See the packet format comment at the top of
        /// the UDP INPUT RECEIVE region.
        /// </summary>
        private void ParseIncomingInputPacket(byte[] data, int length)
        {
            if (TryParseDs4iInputPacket(data, length))
                return;

            TryParseAimmyDsucInputPacket(data, length);
        }

        private bool TryParseDs4iInputPacket(byte[] data, int length)
        {
            // Must be at least 16 bytes and start with magic "DS4I"
            if (length < 16) return false;
            if (data[0] != 'D' || data[1] != 'S' || data[2] != '4' || data[3] != 'I') return false;

            byte padIndex = data[14];
            if (padIndex >= NUMBER_SLOTS) return false;

            byte buttons1    = data[4];   // DpadLeft|DpadDown|DpadRight|DpadUp|Options|R3|L3|Share
            byte buttons2    = data[5];   // Square|Cross|Circle|Triangle|R1|L1|R2Btn|L2Btn
            byte psButton    = data[6];
            byte touchButton = data[7];
            byte lx          = data[8];
            byte ly          = data[9];
            byte rx          = data[10];
            byte ry          = data[11];
            byte l2          = data[12];
            byte r2          = data[13];

            StoreUdpInjectedState(padIndex, buttons1, buttons2, psButton, touchButton, lx, ly, rx, ry, l2, r2);
            return true;
        }

        private bool TryParseAimmyDsucInputPacket(byte[] data, int length)
        {
            // Aimmy 2.5.x sends an 80-byte "DSUC" packet containing controller
            // state. DS4Windows' normal DSU server expects DSUC requests, so
            // accept this compact input packet before the request parser rejects
            // it for having a non-standard payload length.
            if (length < 30) return false;
            if (data[0] != 'D' || data[1] != 'S' || data[2] != 'U' || data[3] != 'C') return false;

            ushort protocolVer = BitConverter.ToUInt16(data, 4);
            if (protocolVer > MaxProtocolVersion) return false;

            uint messageType = BitConverter.ToUInt32(data, 16);
            if (messageType != (uint)MessageType.DSUC_PadDataReq) return false;

            byte padIndex = data[20];
            if (padIndex >= NUMBER_SLOTS) return false;

            byte buttons1 = data[22];
            byte buttons2 = data[23];
            byte psButton = data[24];
            byte touchButton = data[25];
            byte lx = data[26];
            byte ly = data[27];
            byte rx = data[28];
            byte ry = data[29];
            byte l2 = length > 41 ? data[41] : (byte)0;
            byte r2 = length > 40 ? data[40] : (byte)0;

            StoreUdpInjectedState(padIndex, buttons1, buttons2, psButton, touchButton, lx, ly, rx, ry, l2, r2);
            return true;
        }

        private void StoreUdpInjectedState(byte padIndex, byte buttons1, byte buttons2,
            byte psButton, byte touchButton, byte lx, byte ly, byte rx, byte ry, byte l2, byte r2)
        {
            lock (udpInjectedStateLocks[padIndex])
            {
                DS4State s = udpInjectedStates[padIndex];

                // D-pad
                s.DpadLeft  = (buttons1 & 0x80) != 0;
                s.DpadDown  = (buttons1 & 0x40) != 0;
                s.DpadRight = (buttons1 & 0x20) != 0;
                s.DpadUp    = (buttons1 & 0x10) != 0;

                // Menu / stick clicks
                s.Options   = (buttons1 & 0x08) != 0;
                s.R3        = (buttons1 & 0x04) != 0;
                s.L3        = (buttons1 & 0x02) != 0;
                s.Share     = (buttons1 & 0x01) != 0;

                // Face buttons
                s.Square    = (buttons2 & 0x80) != 0;
                s.Cross     = (buttons2 & 0x40) != 0;
                s.Circle    = (buttons2 & 0x20) != 0;
                s.Triangle  = (buttons2 & 0x10) != 0;

                // Shoulder / triggers (digital bits)
                s.R1     = (buttons2 & 0x08) != 0;
                s.L1     = (buttons2 & 0x04) != 0;
                s.R2Btn  = (buttons2 & 0x02) != 0;
                s.L2Btn  = (buttons2 & 0x01) != 0;

                // Special buttons
                s.PS          = psButton    != 0;
                s.TouchButton = touchButton != 0;

                // Analog sticks (128 = center, raw, not pre-inverted)
                s.LX = lx;
                s.LY = ly;
                s.RX = rx;
                s.RY = ry;

                // Analog triggers
                s.L2 = l2;
                s.R2 = r2;
                udpInjectedStateTimes[padIndex] = DateTime.UtcNow;
            }

            DateTime now = DateTime.UtcNow;
            if ((now - lastUdpInputLogTime).TotalSeconds >= 1)
            {
                lastUdpInputLogTime = now;
                AppLogger.LogToGui($"UDP input received for slot {padIndex + 1}: RX={rx} RY={ry}", false, true);
            }

            // Fire the event for anyone listening (e.g. ControlService)
            UdpInputReceived?.Invoke(padIndex, udpInjectedStates[padIndex]);
        }

        /// <summary>
        /// Blocking receive loop run on a dedicated background thread.
        /// Listens on udpInputSock for DS4I input packets.
        /// </summary>
        private void InputReceiveLoop()
        {
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (running && udpInputSock != null)
            {
                try
                {
                    int received = udpInputSock.ReceiveFrom(inputRecvBuffer, ref remoteEP);
                    if (received > 0)
                    {
                        byte[] copy = new byte[received];
                        Array.Copy(inputRecvBuffer, copy, received);
                        ParseIncomingInputPacket(copy, received);
                    }
                }
                catch (SocketException sex)
                {
                    // Socket was closed cleanly on Stop() – exit the loop
                    if (!running || sex.SocketErrorCode == SocketError.Interrupted
                                 || sex.SocketErrorCode == SocketError.OperationAborted)
                        break;

                    // Otherwise just keep going; transient errors shouldn't kill the loop
                }
                catch (Exception)
                {
                    // Swallow unexpected errors so the receive loop stays alive
                }
            }
        }

        /// <summary>
        /// Starts the UDP input receive socket and its background thread.
        /// Called automatically by Start(); you do not need to call this manually.
        /// </summary>
        private void StartInputReceive(int inputPort, IPAddress listenAddress)
        {
            if (!UdpInputEnabled) return;

            try
            {
                udpInputSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                // Reuse the same CONNRESET trick to avoid memory leaks on .NET 6+
                uint IOC_IN = 0x80000000;
                uint IOC_VENDOR = 0x18000000;
                uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                udpInputSock.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);

                udpInputSock.Bind(new IPEndPoint(listenAddress, inputPort));

                inputReceiveThread = new Thread(InputReceiveLoop)
                {
                    IsBackground = true,
                    Name = "DS4W-UdpInputReceive"
                };
                inputReceiveThread.Start();
            }
            catch (SocketException)
            {
                // If the input port is in use, log and carry on – the main DSU
                // server will still work fine.
                udpInputSock?.Close();
                udpInputSock = null;
            }
        }

        /// <summary>
        /// Stops and disposes the UDP input receive socket and thread.
        /// Called automatically by Stop().
        /// </summary>
        private void StopInputReceive()
        {
            if (udpInputSock != null)
            {
                udpInputSock.Close();
                udpInputSock = null;
            }
            inputReceiveThread?.Join(500);
            inputReceiveThread = null;
        }

        // -----------------------------------------------------------------------
        // END UDP INPUT RECEIVE IMPLEMENTATION
        // -----------------------------------------------------------------------

        public void Start(int port, string listenAddress = "")
        {
            if (running)
            {
                if (udpSock != null)
                {
                    udpSock.Close();
                    udpSock = null;
                }
                running = false;
            }

            udpSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPAddress udpListenIPAddress;
            try
            {
                if (listenAddress == "127.0.0.1" || listenAddress == "")
                {
                    udpListenIPAddress = IPAddress.Loopback;
                }
                else if (listenAddress == "0.0.0.0")
                {
                    udpListenIPAddress = IPAddress.Any;
                }
                else
                {
                    IPAddress[] ipAddresses = Dns.GetHostAddresses(listenAddress);
                    udpListenIPAddress = null;
                    foreach (IPAddress ip4 in ipAddresses.Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                    {
                        udpListenIPAddress = ip4;
                        break;
                    }
                    if (udpListenIPAddress == null) throw new SocketException(10049 /*WSAEADDRNOTAVAIL*/);
                }
                udpSock.Bind(new IPEndPoint(udpListenIPAddress, port));
            }
            catch (SocketException ex)
            {
                udpSock.Close();
                udpSock = null;
                throw ex;
            }

            byte[] randomBuf = new byte[4];
            new Random().NextBytes(randomBuf);
            serverId = BitConverter.ToUInt32(randomBuf, 0);

            running = true;
            StartReceive();

            // Start the UDP input receive listener on port+1 (default 26761)
            // using the same listen address as the main DSU server.
            StartInputReceive(DEFAULT_INPUT_PORT, udpListenIPAddress);
        }

        public void Stop()
        {
            running = false;
            if (udpSock != null)
            {
                udpSock.Close();
                udpSock = null;
            }
            StopInputReceive();
        }

        private bool ReportToBuffer(DS4State hidReport, byte[] outputData, ref int outIdx)
        {
            unchecked
            {
                outputData[outIdx] = 0;

                if (hidReport.DpadLeft) outputData[outIdx] |= 0x80;
                if (hidReport.DpadDown) outputData[outIdx] |= 0x40;
                if (hidReport.DpadRight) outputData[outIdx] |= 0x20;
                if (hidReport.DpadUp) outputData[outIdx] |= 0x10;

                if (hidReport.Options) outputData[outIdx] |= 0x08;
                if (hidReport.R3) outputData[outIdx] |= 0x04;
                if (hidReport.L3) outputData[outIdx] |= 0x02;
                if (hidReport.Share) outputData[outIdx] |= 0x01;

                outputData[++outIdx] = 0;

                if (hidReport.Square) outputData[outIdx] |= 0x80;
                if (hidReport.Cross) outputData[outIdx] |= 0x40;
                if (hidReport.Circle) outputData[outIdx] |= 0x20;
                if (hidReport.Triangle) outputData[outIdx] |= 0x10;

                if (hidReport.R1) outputData[outIdx] |= 0x08;
                if (hidReport.L1) outputData[outIdx] |= 0x04;
                if (hidReport.R2Btn) outputData[outIdx] |= 0x02;
                if (hidReport.L2Btn) outputData[outIdx] |= 0x01;

                outputData[++outIdx] = (hidReport.PS) ? (byte)1 : (byte)0;
                outputData[++outIdx] = (hidReport.TouchButton) ? (byte)1 : (byte)0;

                //Left stick
                outputData[++outIdx] = hidReport.LX;
                outputData[++outIdx] = hidReport.LY;
                outputData[outIdx] = (byte)(255 - outputData[outIdx]); //invert Y by convention

                //Right stick
                outputData[++outIdx] = hidReport.RX;
                outputData[++outIdx] = hidReport.RY;
                outputData[outIdx] = (byte)(255 - outputData[outIdx]); //invert Y by convention

                //we don't have analog buttons on DS4 :(
                outputData[++outIdx] = hidReport.DpadLeft ? (byte)0xFF : (byte)0x00;
                outputData[++outIdx] = hidReport.DpadDown ? (byte)0xFF : (byte)0x00;
                outputData[++outIdx] = hidReport.DpadRight ? (byte)0xFF : (byte)0x00;
                outputData[++outIdx] = hidReport.DpadUp ? (byte)0xFF : (byte)0x00;

                outputData[++outIdx] = hidReport.Square ? (byte)0xFF : (byte)0x00;
                outputData[++outIdx] = hidReport.Cross ? (byte)0xFF : (byte)0x00;
                outputData[++outIdx] = hidReport.Circle ? (byte)0xFF : (byte)0x00;
                outputData[++outIdx] = hidReport.Triangle ? (byte)0xFF : (byte)0x00;

                outputData[++outIdx] = hidReport.R1 ? (byte)0xFF : (byte)0x00;
                outputData[++outIdx] = hidReport.L1 ? (byte)0xFF : (byte)0x00;

                outputData[++outIdx] = hidReport.R2;
                outputData[++outIdx] = hidReport.L2;

                outIdx++;

                //DS4 only: touchpad points
                for (int i = 0; i < 2; i++)
                {
                    var tpad = (i == 0) ? hidReport.TrackPadTouch0 : hidReport.TrackPadTouch1;

                    outputData[outIdx++] = tpad.IsActive ? (byte)1 : (byte)0;
                    outputData[outIdx++] = (byte)tpad.Id;
                    Array.Copy(BitConverter.GetBytes((ushort)tpad.X), 0, outputData, outIdx, 2);
                    outIdx += 2;
                    Array.Copy(BitConverter.GetBytes((ushort)tpad.Y), 0, outputData, outIdx, 2);
                    outIdx += 2;
                }

                //motion timestamp
                if (hidReport.Motion != null)
                    Array.Copy(BitConverter.GetBytes((ulong)hidReport.totalMicroSec), 0, outputData, outIdx, 8);
                else
                    Array.Clear(outputData, outIdx, 8);

                outIdx += 8;

                //accelerometer
                if (hidReport.Motion != null)
                {
                    Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.accelXG), 0, outputData, outIdx, 4);
                    outIdx += 4;
                    Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.accelYG), 0, outputData, outIdx, 4);
                    outIdx += 4;
                    Array.Copy(BitConverter.GetBytes((float)-hidReport.Motion.accelZG), 0, outputData, outIdx, 4);
                    outIdx += 4;
                }
                else
                {
                    Array.Clear(outputData, outIdx, 12);
                    outIdx += 12;
                }

                //gyroscope
                if (hidReport.Motion != null)
                {
                    Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.angVelPitch), 0, outputData, outIdx, 4);
                    outIdx += 4;
                    Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.angVelYaw), 0, outputData, outIdx, 4);
                    outIdx += 4;
                    Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.angVelRoll), 0, outputData, outIdx, 4);
                    outIdx += 4;
                }
                else
                {
                    Array.Clear(outputData, outIdx, 12);
                    outIdx += 12;
                }
            }

            return true;
        }

        private bool ReportToBufferDataRsp(DS4State hidReport, ref PadDataRspPacket currentRsp)
        {
            unchecked
            {
                currentRsp.buttons1 = 0;
                if (hidReport.DpadLeft) currentRsp.buttons1 |= 0x80;
                if (hidReport.DpadDown) currentRsp.buttons1 |= 0x40;
                if (hidReport.DpadRight) currentRsp.buttons1 |= 0x20;
                if (hidReport.DpadUp) currentRsp.buttons1 |= 0x10;

                if (hidReport.Options) currentRsp.buttons1 |= 0x08;
                if (hidReport.R3) currentRsp.buttons1 |= 0x04;
                if (hidReport.L3) currentRsp.buttons1 |= 0x02;
                if (hidReport.Share) currentRsp.buttons1 |= 0x01;

                currentRsp.buttons2 = 0;

                if (hidReport.Square) currentRsp.buttons2 |= 0x80;
                if (hidReport.Cross) currentRsp.buttons2 |= 0x40;
                if (hidReport.Circle) currentRsp.buttons2  |= 0x20;
                if (hidReport.Triangle) currentRsp.buttons2 |= 0x10;

                if (hidReport.R1) currentRsp.buttons2 |= 0x08;
                if (hidReport.L1) currentRsp.buttons2 |= 0x04;
                if (hidReport.R2Btn) currentRsp.buttons2 |= 0x02;
                if (hidReport.L2Btn) currentRsp.buttons2 |= 0x01;

                currentRsp.psButton = (hidReport.PS) ? (byte)1 : (byte)0;
                currentRsp.touchButton = (hidReport.TouchButton) ? (byte)1 : (byte)0;

                //Left stick
                currentRsp.lx = hidReport.LX;
                currentRsp.ly = hidReport.LY;
                currentRsp.ly = (byte)(255 - currentRsp.ly); //invert Y by convention

                //Right stick
                currentRsp.rx = hidReport.RX;
                currentRsp.ry = hidReport.RY;
                currentRsp.ry = (byte)(255 - currentRsp.ry); //invert Y by convention

                //we don't have analog buttons on DS4 :(
                currentRsp.dpadLeft = hidReport.DpadLeft ? (byte)0xFF : (byte)0x00;
                currentRsp.dpadDown = hidReport.DpadDown ? (byte)0xFF : (byte)0x00;
                currentRsp.dpadRight = hidReport.DpadRight ? (byte)0xFF : (byte)0x00;
                currentRsp.dpadUp = hidReport.DpadUp ? (byte)0xFF : (byte)0x00;

                currentRsp.square = hidReport.Square ? (byte)0xFF : (byte)0x00;
                currentRsp.cross = hidReport.Cross ? (byte)0xFF : (byte)0x00;
                currentRsp.circle = hidReport.Circle ? (byte)0xFF : (byte)0x00;
                currentRsp.triangle = hidReport.Triangle ? (byte)0xFF : (byte)0x00;

                currentRsp.r1 = hidReport.R1 ? (byte)0xFF : (byte)0x00;
                currentRsp.l1 = hidReport.L1 ? (byte)0xFF : (byte)0x00;

                currentRsp.r2 = hidReport.R2;
                currentRsp.l2 = hidReport.L2;

                //DS4 only: touchpad points
                for (int i = 0; i < 2; i++)
                {
                    var tpad = (i == 0) ? hidReport.TrackPadTouch0 : hidReport.TrackPadTouch1;
                    if (i == 0)
                    {
                        currentRsp.touch1Active = tpad.IsActive ? (byte)1 : (byte)0;
                        currentRsp.touch1PacketId = (byte)tpad.Id;
                        currentRsp.touch1X = (ushort)tpad.X;
                        currentRsp.touch1Y = (ushort)tpad.Y;
                    }
                    else if (i == 1)
                    {
                        currentRsp.touch2Active = tpad.IsActive ? (byte)1 : (byte)0;
                        currentRsp.touch2PacketId = (byte)tpad.Id;
                        currentRsp.touch2X = (ushort)tpad.X;
                        currentRsp.touch2Y = (ushort)tpad.Y;
                    }
                }

                //motion timestamp
                if (hidReport.Motion != null)
                    currentRsp.totalMicroSec = hidReport.totalMicroSec;
                else
                    currentRsp.totalMicroSec = 0;

                //accelerometer
                if (hidReport.Motion != null)
                {
                    currentRsp.accelXG = (float)hidReport.Motion.accelXG;
                    currentRsp.accelYG = (float)hidReport.Motion.accelYG;
                    currentRsp.accelZG = (float)-hidReport.Motion.accelZG;
                }
                else
                {
                    currentRsp.accelXG = 0;
                    currentRsp.accelYG = 0;
                    currentRsp.accelZG = 0;
                }

                //gyroscope
                if (hidReport.Motion != null)
                {
                    currentRsp.angVelPitch = (float)hidReport.Motion.angVelPitch;
                    currentRsp.angVelYaw = (float)hidReport.Motion.angVelYaw;
                    currentRsp.angVelRoll = (float)hidReport.Motion.angVelRoll;
                }
                else
                {
                    currentRsp.angVelPitch = 0;
                    currentRsp.angVelYaw = 0;
                    currentRsp.angVelRoll = 0;
                }
            }

            return true;
        }

        public unsafe void NewReportIncoming(ref DualShockPadMeta padMeta, DS4State hidReport, byte[] outputData)
        {
            if (!running)
                return;

            var clientsList = new List<IPEndPoint>();
            var now = DateTime.UtcNow;
            lock (clients)
            {
                var clientsToDelete = new List<IPEndPoint>();

                foreach (var cl in clients)
                {
                    const double TimeoutLimit = 5;

                    if ((now - cl.Value.AllPadsTime).TotalSeconds < TimeoutLimit)
                        clientsList.Add(cl.Key);
                    else if ((padMeta.PadId < cl.Value.PadIdsTime.Length) &&
                             (now - cl.Value.PadIdsTime[(byte)padMeta.PadId]).TotalSeconds < TimeoutLimit)
                        clientsList.Add(cl.Key);
                    else if (cl.Value.PadMacsTime.ContainsKey(padMeta.PadMacAddress) &&
                             (now - cl.Value.PadMacsTime[padMeta.PadMacAddress]).TotalSeconds < TimeoutLimit)
                        clientsList.Add(cl.Key);
                    else //check if this client is totally dead, and remove it if so
                    {
                        bool clientOk = false;
                        for (int i = 0; i < cl.Value.PadIdsTime.Length; i++)
                        {
                            var dur = (now - cl.Value.PadIdsTime[i]).TotalSeconds;
                            if (dur < TimeoutLimit)
                            {
                                clientOk = true;
                                break;
                            }
                        }
                        if (!clientOk)
                        {
                            foreach (var dict in cl.Value.PadMacsTime)
                            {
                                var dur = (now - dict.Value).TotalSeconds;
                                if (dur < TimeoutLimit)
                                {
                                    clientOk = true;
                                    break;
                                }
                            }

                            if (!clientOk)
                                clientsToDelete.Add(cl.Key);
                        }
                    }
                }

                foreach (var delCl in clientsToDelete)
                {
                    clients.Remove(delCl);
                }
                clientsToDelete.Clear();
                clientsToDelete = null;
            }

            if (clientsList.Count <= 0)
                return;

            unchecked
            {
                PadDataRspPacket currentRsp = new PadDataRspPacket();
                BeginDataRspPacket(ref currentRsp, 1001);
                currentRsp.messageType = (uint)MessageType.DSUS_PadDataRsp;

                currentRsp.padId = (byte)padMeta.PadId;
                currentRsp.padState = (byte)padMeta.PadState;
                currentRsp.model = (byte)padMeta.Model;
                currentRsp.connectionType = (byte)padMeta.ConnectionType;
                {
                    byte[] padMac = padMeta.PadMacAddress.GetAddressBytes();
                    currentRsp.address[0] = padMac[0];
                    currentRsp.address[1] = padMac[1];
                    currentRsp.address[2] = padMac[2];
                    currentRsp.address[3] = padMac[3];
                    currentRsp.address[4] = padMac[4];
                    currentRsp.address[5] = padMac[5];
                }
                currentRsp.batteryStatus = (byte)padMeta.BatteryStatus;
                currentRsp.isActive = padMeta.IsActive ? (byte)1 : (byte)0;

                currentRsp.packetCounter = hidReport.PacketCounter;

                if (!ReportToBufferDataRsp(hidReport, ref currentRsp))
                    return;
                else
                    FinishDataRspPacket(ref currentRsp, outputData);

                foreach (var cl in clientsList)
                {
                    int temp = 0;
                    poolLock.EnterWriteLock();
                    temp = listInd;
                    listInd = ++listInd % ARG_BUFFER_LEN;
                    SocketAsyncEventArgs args = new SocketAsyncEventArgs()
                    {
                        RemoteEndPoint = cl,
                    };
                    args.SetBuffer(dataBuffers[temp], 0, 100);
                    args.Completed += SocketEvent_AsyncCompleted;
                    poolLock.ExitWriteLock();

                    _pool.Wait();
                    Array.Copy(outputData, args.Buffer, outputData.Length);
                    bool sentAsync = false;
                    try {
                        sentAsync = udpSock.SendToAsync(args);
                    }
                    catch (SocketException /*ex*/) { }
                    finally
                    {
                        if (!sentAsync) CompletedSynchronousSocketEvent(args);
                    }
                }
            }

            clientsList.Clear();
            clientsList = null;
        }

        private void CopyBytes(ref PadDataRspPacket outReport, byte[] outBuffer, int bufferLen)
        {
            GCHandle h = GCHandle.Alloc(outReport, GCHandleType.Pinned);
            Marshal.Copy(h.AddrOfPinnedObject(), outBuffer, 0, bufferLen);
            h.Free();
        }
    }
}
