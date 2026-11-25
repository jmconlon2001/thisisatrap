<#
.SYNOPSIS
    Sends test messages to ThisIsATrap (HTML body, inline image, and attachments) using raw SMTP.
.EXAMPLE
    .\Send-TestMail.ps1 -Server localhost -Port 25 -Count 3
#>
param(
    [string]$Server = 'localhost',
    [int]$Port = 25,
    [int]$Count = 1,
    [string]$From = 'Build Bot <build-bot@example.com>',
    [string]$To = 'Jane Developer <jane@example.com>',
    [string]$Cc = 'qa-team@example.com',
    [string]$Bcc = 'audit@example.com',
    [string]$User,
    [string]$Password
)

$ErrorActionPreference = 'Stop'

function Read-Reply([System.IO.StreamReader]$reader) {
    do { $line = $reader.ReadLine() } while ($line -and $line.Length -ge 4 -and $line[3] -eq '-')
    if (-not $line) { throw 'Connection closed by server' }
    if ($line[0] -notin '2', '3') { throw "SMTP error: $line" }
    $line
}

function Send-Command($writer, $reader, [string]$cmd) {
    $writer.Write("$cmd`r`n"); $writer.Flush()
    Read-Reply $reader
}

$pngBase64 = 'iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAKElEQVR42mNkYPhfz0AEYBxVSF+FjP8ZGBgYGRkZGBkZiVbIQHeFAACxRgMRqsYb6wAAAABJRU5ErkJggg=='
$csv = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("id,name,amount`r`n1,Alpha,10.00`r`n2,Beta,22.50`r`n"))
$pdf = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj 3 0 obj<</Type/Page/MediaBox[0 0 200 100]/Parent 2 0 R>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF"))

function Split-Base64([string]$b64) { ($b64 -split '(.{76})' | Where-Object { $_ }) -join "`r`n" }

function Get-Address([string]$s) { if ($s -match '<([^>]+)>') { $Matches[1] } else { $s } }

for ($n = 1; $n -le $Count; $n++) {
    $client = [System.Net.Sockets.TcpClient]::new($Server, $Port)
    $stream = $client.GetStream()
    $reader = [System.IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
    $writer = [System.IO.StreamWriter]::new($stream, [Text.Encoding]::ASCII)
    try {
        Read-Reply $reader | Out-Null
        Send-Command $writer $reader "EHLO $env:COMPUTERNAME" | Out-Null
        if ($User) {
            $plain = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("`0$User`0$Password"))
            Send-Command $writer $reader "AUTH PLAIN $plain" | Out-Null
        }
        Send-Command $writer $reader "MAIL FROM:<$(Get-Address $From)>" | Out-Null
        foreach ($rcpt in @($To, $Cc, $Bcc) | Where-Object { $_ }) { Send-Command $writer $reader "RCPT TO:<$(Get-Address $rcpt)>" | Out-Null }
        Send-Command $writer $reader 'DATA' | Out-Null

        $stamp = Get-Date -Format 'HH:mm:ss'
        $boundary = "mix_$([guid]::NewGuid().ToString('N'))"
        $alt = "alt_$([guid]::NewGuid().ToString('N'))"
        $rel = "rel_$([guid]::NewGuid().ToString('N'))"
        $body = @"
From: $From
To: $To
Cc: $Cc
Subject: Build #$((Get-Random -Minimum 1000 -Maximum 9999)) succeeded ($n of $Count, $stamp)
Date: $((Get-Date).ToString('r'))
Message-ID: <$([guid]::NewGuid())@example.com>
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="$boundary"

--$boundary
Content-Type: multipart/alternative; boundary="$alt"

--$alt
Content-Type: text/plain; charset=utf-8

Hello Jane,

The nightly build finished successfully. See https://example.com/builds/latest for details.

-- Build Bot
--$alt
Content-Type: multipart/related; boundary="$rel"

--$rel
Content-Type: text/html; charset=utf-8

<html><head><style>body{font-family:Segoe UI,Arial,sans-serif;color:#0f172a} .card{border:1px solid #e5e8ef;border-radius:12px;padding:16px;max-width:520px} .ok{color:#059669;font-weight:700}</style></head>
<body><div class="card"><p><img src="cid:logo@example.com" width="32" height="32" alt="logo" style="vertical-align:middle"> <span class="ok">Build succeeded</span></p>
<p>Hello Jane,</p><p>The nightly build finished in <b>4m 12s</b>. <a href="https://example.com/builds/latest">View the build</a>.</p>
<table border="1" cellpadding="6" style="border-collapse:collapse"><tr><th>Project</th><th>Tests</th></tr><tr><td>Api</td><td>412 passed</td></tr><tr><td>Web</td><td>128 passed</td></tr></table>
<script>alert('scripts must not run')</script>
</div></body></html>
--$rel
Content-Type: image/png
Content-ID: <logo@example.com>
Content-Disposition: inline; filename="logo.png"
Content-Transfer-Encoding: base64

$(Split-Base64 $pngBase64)
--$rel--
--$alt--
--$boundary
Content-Type: text/csv; name="report.csv"
Content-Disposition: attachment; filename="report.csv"
Content-Transfer-Encoding: base64

$(Split-Base64 $csv)
--$boundary
Content-Type: application/pdf; name="summary.pdf"
Content-Disposition: attachment; filename="summary.pdf"
Content-Transfer-Encoding: base64

$(Split-Base64 $pdf)
--$boundary--
"@
        # Dot-stuff lines that start with '.', then terminate DATA.
        $body = ($body -split "`r?`n" | ForEach-Object { if ($_.StartsWith('.')) { ".$_" } else { $_ } }) -join "`r`n"
        $writer.Write("$body`r`n.`r`n"); $writer.Flush()
        $reply = Read-Reply $reader
        Send-Command $writer $reader 'QUIT' | Out-Null
        Write-Host "[$n/$Count] $reply"
    }
    finally { $client.Dispose() }
}
