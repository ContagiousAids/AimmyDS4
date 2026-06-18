param(
    [string]$ip = '127.0.0.1',
    [int]$port = 26760,
    [int]$count = 5,
    [int]$rightx = 200,
    [int]$righty = 55,
    [bool]$usecrc = $true,
    [uint32]$clientid = 1
)

function Get-Crc32Table {
    $poly = 0xEDB88320
    $table = New-Object 'object[]' 256
    for ($i = 0; $i -lt 256; $i++) {
        $crc = [uint32]$i
        for ($j = 0; $j -lt 8; $j++) {
            if (($crc -band 1) -eq 1) {
                $crc = (($crc -shr 1) -bxor $poly)
            } else {
                $crc = ($crc -shr 1)
            }
        }
        $table[$i] = $crc
    }
    return $table
}

$CRC_TABLE = Get-Crc32Table

function Compute-Crc32 {
    param(
        [byte[]]$data
    )

    $crc = [uint32]0xFFFFFFFF
    foreach ($b in $data) {
        $idx = ($crc -bxor [uint32]$b) -band 0xFF
        $crc = $CRC_TABLE[$idx] -bxor ($crc -shr 8)
    }
    return ($crc -bxor 0xFFFFFFFF)
}

$client = New-Object System.Net.Sockets.UdpClient
for ($i = 0; $i -lt $count; $i++) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('DSUC'))
    $bw.Write([uint16]1001)
    $bw.Write([uint16]76)

    # CRC placeholder (filled later)
    $bw.Write([uint32]0)

    # Client ID
    $bw.Write([uint32]$clientid)

    $bw.Write([uint32]0x100002)

    $bw.Write([byte]0)
    $bw.Write([byte]2)
    $bw.Write([uint16]0)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([byte]128)
    $bw.Write([byte]128)

    $bw.Write([byte]([byte]$rightx))
    $bw.Write([byte]([byte]$righty))

    for ($j = 0; $j -lt 12; $j++) { $bw.Write([byte]0) }
    for ($j = 0; $j -lt 6; $j++) { $bw.Write([byte]0) }

    $bw.Write([uint64]($i * 1000))

    $bw.Write([float]0)
    $bw.Write([float]0)
    $bw.Write([float]1)
    $bw.Write([float]0)
    $bw.Write([float]0)
    $bw.Write([float]0)

    $packet = $ms.ToArray()

    if ($usecrc) {
        # CRC field offset = 8 bytes ("DSUC"=4 + ver=2 + len=2)
        $crcOffset = 8
        $crcSize = 4

        $buf = New-Object byte[] ($packet.Length)
        [Array]::Copy($packet, $buf, $packet.Length)

        # CRC computed with CRC field treated as 0
        for ($k = 0; $k -lt $crcSize; $k++) { $buf[$crcOffset + $k] = 0 }

        $crc = Compute-Crc32 -data $buf

        # write little-endian CRC
        $packet[$crcOffset + 0] = [byte]($crc -band 0xFF)
        $packet[$crcOffset + 1] = [byte](($crc -shr 8) -band 0xFF)
        $packet[$crcOffset + 2] = [byte](($crc -shr 16) -band 0xFF)
        $packet[$crcOffset + 3] = [byte](($crc -shr 24) -band 0xFF)
    }

    try {
        $client.Send($packet, $packet.Length, $ip, $port) | Out-Null
        Write-Host ("[{0}] Sent {1} bytes to {2}:{3} (rightX={4} rightY={5} clientid={6} usecrc={7})" -f $i, $packet.Length, $ip, $port, $rightx, $righty, $clientid, $usecrc)
    } catch {
        Write-Host "[$i] Send failed: $_"
    }
    Start-Sleep -Milliseconds 50
}

$client.Close()
Write-Host 'Done'
