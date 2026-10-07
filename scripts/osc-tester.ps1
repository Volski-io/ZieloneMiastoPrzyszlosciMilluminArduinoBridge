param(
    [string]$TargetHost = "127.0.0.1",
    [ValidateRange(1, 65535)]
    [int]$Port = 5001,
    [string]$Address = "/makieta/balon/2/predkosc/70",
    [string]$ArgumentsJson = "[]",
    [switch]$SendOnce
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Add-OscString {
    param(
        [System.Collections.Generic.List[byte]]$Buffer,
        [string]$Value
    )

    foreach ($item in [System.Text.Encoding]::UTF8.GetBytes($Value)) {
        $Buffer.Add($item)
    }
    $Buffer.Add(0)
    while (($Buffer.Count % 4) -ne 0) {
        $Buffer.Add(0)
    }
}

function Add-BigEndianBytes {
    param(
        [System.Collections.Generic.List[byte]]$Buffer,
        [byte[]]$Bytes
    )

    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($Bytes)
    }
    $Buffer.AddRange($Bytes)
}

function ConvertFrom-ArgumentsJson {
    param([string]$Json)

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return ,@()
    }

    $parsed = ConvertFrom-Json -InputObject $Json
    if ($parsed -isnot [System.Array]) {
        throw "Argumenty musza byc tablica JSON, np. [], [84] albo [1, `"Scena 1`"]."
    }
    return ,@($parsed)
}

function New-OscPacket {
    param(
        [string]$OscAddress,
        [object[]]$Arguments
    )

    if (-not $OscAddress.StartsWith('/')) {
        throw "Adres OSC musi zaczynac sie od '/', np. /ping."
    }

    $buffer = [System.Collections.Generic.List[byte]]::new()
    $typeTags = [System.Text.StringBuilder]::new(',')

    foreach ($argument in $Arguments) {
        if ($argument -is [bool]) {
            [void]$typeTags.Append($(if ($argument) { 'T' } else { 'F' }))
        }
        elseif ($argument -is [byte] -or $argument -is [sbyte] -or
                $argument -is [int16] -or $argument -is [uint16] -or
                $argument -is [int32] -or $argument -is [uint32] -or
                $argument -is [int64] -or $argument -is [uint64]) {
            if ([decimal]$argument -lt [int]::MinValue -or [decimal]$argument -gt [int]::MaxValue) {
                throw "Liczba calkowita $argument nie miesci sie w OSC int32."
            }
            [void]$typeTags.Append('i')
        }
        elseif ($argument -is [single] -or $argument -is [double] -or $argument -is [decimal]) {
            [void]$typeTags.Append('f')
        }
        elseif ($argument -is [string]) {
            [void]$typeTags.Append('s')
        }
        else {
            throw "Nieobslugiwany typ argumentu OSC: $($argument.GetType().FullName)."
        }
    }

    Add-OscString -Buffer $buffer -Value $OscAddress
    Add-OscString -Buffer $buffer -Value $typeTags.ToString()

    foreach ($argument in $Arguments) {
        if ($argument -is [bool]) {
            continue
        }
        if ($argument -is [string]) {
            Add-OscString -Buffer $buffer -Value $argument
            continue
        }
        if ($argument -is [single] -or $argument -is [double] -or $argument -is [decimal]) {
            Add-BigEndianBytes -Buffer $buffer -Bytes ([BitConverter]::GetBytes([single]$argument))
            continue
        }
        Add-BigEndianBytes -Buffer $buffer -Bytes ([BitConverter]::GetBytes([int]$argument))
    }

    return $buffer.ToArray()
}

function Send-OscMessage {
    param(
        [string]$Destination,
        [int]$DestinationPort,
        [string]$OscAddress,
        [string]$Json
    )

    $arguments = ConvertFrom-ArgumentsJson -Json $Json
    $packet = New-OscPacket -OscAddress $OscAddress -Arguments $arguments
    $client = [System.Net.Sockets.UdpClient]::new()
    try {
        $sent = $client.Send($packet, $packet.Length, $Destination, $DestinationPort)
    }
    finally {
        $client.Dispose()
    }

    $raw = ($packet | ForEach-Object { $_.ToString('X2') }) -join ' '
    return [pscustomobject]@{
        Bytes = $sent
        Raw = $raw
        Arguments = $arguments
    }
}

if ($SendOnce) {
    $result = Send-OscMessage `
        -Destination $TargetHost `
        -DestinationPort $Port `
        -OscAddress $Address `
        -Json $ArgumentsJson
    Write-Host "Wyslano OSC do ${TargetHost}:$Port"
    Write-Host "Adres: $Address"
    Write-Host "Argumenty: $ArgumentsJson"
    Write-Host "Raw bytes ($($result.Bytes)): $($result.Raw)"
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$form = [System.Windows.Forms.Form]::new()
$form.Text = "OSC Tester - Millumin UART Bridge"
$form.ClientSize = [System.Drawing.Size]::new(720, 510)
$form.MinimumSize = [System.Drawing.Size]::new(620, 500)
$form.StartPosition = "CenterScreen"
$form.Font = [System.Drawing.Font]::new("Segoe UI", 10)

$title = [System.Windows.Forms.Label]::new()
$title.Text = "Wyslij testowa wiadomosc OSC"
$title.Font = [System.Drawing.Font]::new("Segoe UI Semibold", 16)
$title.AutoSize = $true
$title.Location = [System.Drawing.Point]::new(24, 20)
$form.Controls.Add($title)

$hint = [System.Windows.Forms.Label]::new()
$hint.Text = "Argumenty wpisuj jako tablice JSON, np. [], [84] lub [1, `"Scena 1`"]."
$hint.AutoSize = $true
$hint.ForeColor = [System.Drawing.Color]::DimGray
$hint.Location = [System.Drawing.Point]::new(27, 55)
$form.Controls.Add($hint)

function Add-Field {
    param([string]$Caption, [int]$Top, [int]$Left, [int]$Width, [string]$Value)

    $label = [System.Windows.Forms.Label]::new()
    $label.Text = $Caption
    $label.AutoSize = $true
    $label.Location = [System.Drawing.Point]::new($Left, $Top)
    $form.Controls.Add($label)

    $box = [System.Windows.Forms.TextBox]::new()
    $box.Text = $Value
    $box.Location = [System.Drawing.Point]::new($Left, $Top + 23)
    $box.Size = [System.Drawing.Size]::new($Width, 28)
    $form.Controls.Add($box)
    return $box
}

$hostBox = Add-Field -Caption "Komputer docelowy / IP" -Top 92 -Left 26 -Width 330 -Value $TargetHost
$portBox = Add-Field -Caption "Port UDP" -Top 92 -Left 378 -Width 150 -Value $Port.ToString()
$addressBox = Add-Field -Caption "Adres OSC" -Top 164 -Left 26 -Width 502 -Value $Address
$argumentsBox = Add-Field -Caption "Argumenty JSON" -Top 236 -Left 26 -Width 502 -Value $ArgumentsJson

$sendButton = [System.Windows.Forms.Button]::new()
$sendButton.Text = "Wyslij OSC"
$sendButton.Location = [System.Drawing.Point]::new(552, 187)
$sendButton.Size = [System.Drawing.Size]::new(140, 99)
$sendButton.BackColor = [System.Drawing.Color]::FromArgb(50, 150, 110)
$sendButton.ForeColor = [System.Drawing.Color]::White
$sendButton.FlatStyle = "Flat"
$form.Controls.Add($sendButton)

$presetBridge = [System.Windows.Forms.Button]::new()
$presetBridge.Text = "Preset: balon 2 / 70%"
$presetBridge.Location = [System.Drawing.Point]::new(26, 306)
$presetBridge.Size = [System.Drawing.Size]::new(180, 34)
$form.Controls.Add($presetBridge)

$presetFeedback = [System.Windows.Forms.Button]::new()
$presetFeedback.Text = "Preset: slupy sektor 3"
$presetFeedback.Location = [System.Drawing.Point]::new(216, 306)
$presetFeedback.Size = [System.Drawing.Size]::new(210, 34)
$form.Controls.Add($presetFeedback)

$presetPing = [System.Windows.Forms.Button]::new()
$presetPing.Text = "Preset: /ping"
$presetPing.Location = [System.Drawing.Point]::new(436, 306)
$presetPing.Size = [System.Drawing.Size]::new(140, 34)
$form.Controls.Add($presetPing)

$logBox = [System.Windows.Forms.TextBox]::new()
$logBox.Multiline = $true
$logBox.ReadOnly = $true
$logBox.ScrollBars = "Vertical"
$logBox.Font = [System.Drawing.Font]::new("Consolas", 9)
$logBox.Location = [System.Drawing.Point]::new(26, 358)
$logBox.Size = [System.Drawing.Size]::new(666, 124)
$logBox.Anchor = "Top, Bottom, Left, Right"
$form.Controls.Add($logBox)

$presetBridge.Add_Click({
    $hostBox.Text = "127.0.0.1"
    $portBox.Text = "5001"
    $addressBox.Text = "/makieta/balon/2/predkosc/70"
    $argumentsBox.Text = "[]"
})

$presetFeedback.Add_Click({
    $hostBox.Text = "127.0.0.1"
    $portBox.Text = "5001"
    $addressBox.Text = "/makieta/slupy/sektor/3/on"
    $argumentsBox.Text = "[]"
})

$presetPing.Add_Click({
    $portBox.Text = "5000"
    $addressBox.Text = "/ping"
    $argumentsBox.Text = "[]"
})

$sendButton.Add_Click({
    try {
        $destinationPort = 0
        if (-not [int]::TryParse($portBox.Text, [ref]$destinationPort) -or
            $destinationPort -lt 1 -or $destinationPort -gt 65535) {
            throw "Port musi byc liczba od 1 do 65535."
        }

        $result = Send-OscMessage `
            -Destination $hostBox.Text.Trim() `
            -DestinationPort $destinationPort `
            -OscAddress $addressBox.Text.Trim() `
            -Json $argumentsBox.Text.Trim()

        $timestamp = Get-Date -Format "HH:mm:ss.fff"
        $logBox.AppendText("[$timestamp] TX $($hostBox.Text):$destinationPort $($addressBox.Text) $($argumentsBox.Text)`r`n")
        $logBox.AppendText("RAW: $($result.Raw)`r`n`r`n")
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show(
            $_.Exception.Message,
            "Blad OSC",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
})

[void]$form.ShowDialog()
