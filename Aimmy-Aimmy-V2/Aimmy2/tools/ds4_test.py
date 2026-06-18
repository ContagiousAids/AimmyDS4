#!/usr/bin/env python3
import socket
import struct
import time
import argparse

parser = argparse.ArgumentParser(description='Send DS4Windows DSUC test packets')
parser.add_argument('--ip', default='127.0.0.1')
parser.add_argument('--port', type=int, default=26760)
parser.add_argument('--count', type=int, default=3)
parser.add_argument('--rightx', type=int, default=200)
parser.add_argument('--righty', type=int, default=55)
parser.add_argument('--usecrc', action='store_true', help='Compute and fill CRC32 (matches Aimmy default)')
parser.add_argument('--clientid', type=int, default=1, help='DS4Windows client ID')
args = parser.parse_args()

addr = (args.ip, args.port)
print(f"Sending {args.count} DSUC packets to {addr}")

# CRC32 implementation matching Aimmy2/InputLogic/Crc32.cs
POLY = 0xEDB88320
TABLE = []
for i in range(256):
    crc = i
    for _ in range(8):
        crc = (crc >> 1) ^ POLY if (crc & 1) else (crc >> 1)
    TABLE.append(crc & 0xFFFFFFFF)

def crc32(data: bytes) -> int:
    crc = 0xFFFFFFFF
    for b in data:
        crc = TABLE[(crc ^ b) & 0xFF] ^ (crc >> 8)
    return (crc ^ 0xFFFFFFFF) & 0xFFFFFFFF

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
for i in range(args.count):
    rightx = args.rightx & 0xFF
    righty = args.righty & 0xFF
    client_id = args.clientid & 0xFFFFFFFF

    # Build packet matching DS4WInputService.BuildPacket
    parts = []
    parts.append(b'DSUC')
    parts.append(struct.pack('<H', 1001))   # protocol version
    parts.append(struct.pack('<H', 76))     # packet length (as in C# code)
    parts.append(struct.pack('<I', 0))      # CRC placeholder (filled later)
    parts.append(struct.pack('<I', client_id))  # Client ID
    parts.append(struct.pack('<I', 0x100002))
    parts.append(struct.pack('<B', 0))      # Pad ID
    parts.append(struct.pack('<B', 2))      # Pad state
    parts.append(struct.pack('<H', 0))      # Buttons
    parts.append(struct.pack('<B', 0))      # PS
    parts.append(struct.pack('<B', 0))      # Touch
    parts.append(struct.pack('<B', 128))    # Left stick X
    parts.append(struct.pack('<B', 128))    # Left stick Y
    parts.append(struct.pack('<B', rightx)) # Right stick X
    parts.append(struct.pack('<B', righty)) # Right stick Y
    # 12 analog face bytes + 6 touch bytes packed in C# as separate sections:
    parts.append(bytes([0]*12))           # analog face bytes
    parts.append(bytes([0]*6))            # touch bytes
    parts.append(struct.pack('<Q', i*1000))  # Timestamp
    parts.append(struct.pack('<6f', 0.0, 0.0, 1.0, 0.0, 0.0, 0.0))  # accel

    packet = b''.join(parts)

    if args.usecrc:
        # CRC field offset = 8 bytes ("DSUC"=4 + ver=2 + len=2) then CRC uint32 starts
        crc_offset = 8
        crc_size = 4

        buf = bytearray(packet)
        # CRC is computed with CRC field treated as 0
        for j in range(crc_size):
            buf[crc_offset + j] = 0

        c = crc32(bytes(buf))
        buf[crc_offset + 0] = c & 0xFF
        buf[crc_offset + 1] = (c >> 8) & 0xFF
        buf[crc_offset + 2] = (c >> 16) & 0xFF
        buf[crc_offset + 3] = (c >> 24) & 0xFF
        packet = bytes(buf)

    try:
        sent = sock.sendto(packet, addr)
        print(f"[{i}] Sent {sent} bytes to {addr} (rightX={rightx} rightY={righty} clientid={client_id} usecrc={args.usecrc})")
    except Exception as e:
        print(f"[{i}] Send failed: {e}")

    time.sleep(0.05)

sock.close()
print('Done')
