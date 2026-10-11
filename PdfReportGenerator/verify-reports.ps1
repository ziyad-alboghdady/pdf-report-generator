<#
  verify-reports.ps1 - the checkpoints from the BE-08 brief, as assertions.

  Starts the API itself, runs every check, stops it again. Nothing to set up
  in a second terminal.

      dotnet run -- seed        (once, if you have not already)
      .\verify-reports.ps1

  Note on how requests are sent: PowerShell 5.1 strips quotes out of arguments
  it hands to native executables, which mangles inline JSON. Every body here
  is written to a temp file and passed with --data-binary "@file" instead.
#>

$ErrorActionPreference = "Stop"
$here       = $PSScriptRoot
$baseUrl    = "http://localhost:5080"
$reportsDir = Join-Path $here "reports"

$passed = 0
$failed = 0

function Check($name, $condition, $detail) {
    if ($condition) {
        Write-Host ("  PASS  " + $name) -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host ("  FAIL  " + $name) -ForegroundColor Red
        if ($detail) { Write-Host ("        " + $detail) -ForegroundColor DarkGray }
        $script:failed++
    }
}

function Section($title) {
    Write-Host ""
    Write-Host $title -ForegroundColor Cyan
}

# One curl call returns status, body and headers together. Calling curl twice
# for the same logical request would POST twice - which, in a test about not
# generating two reports, would be the bug testing for itself.
function Send($method, $path, $json) {
    $bodyFile    = [System.IO.Path]::GetTempFileName()
    $headerFile  = [System.IO.Path]::GetTempFileName()
    $curlArgs = @("-s", "-o", $bodyFile, "-D", $headerFile, "-w", "%{http_code}", "-X", $method, "$baseUrl$path")

    $payloadFile = $null
    if ($json) {
        $payloadFile = [System.IO.Path]::GetTempFileName()
        [System.IO.File]::WriteAllText($payloadFile, $json)
        $curlArgs += @("-H", "Content-Type: application/json", "--data-binary", "@$payloadFile")
    }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $code  = (& curl.exe @curlArgs) | Select-Object -Last 1
    $watch.Stop()

    $body    = Get-Content $bodyFile -Raw -ErrorAction SilentlyContinue
    $headers = Get-Content $headerFile -Raw -ErrorAction SilentlyContinue
    Remove-Item $bodyFile, $headerFile -Force -ErrorAction SilentlyContinue
    if ($payloadFile) { Remove-Item $payloadFile -Force -ErrorAction SilentlyContinue }

    $parsed = $null
    if ($body) { try { $parsed = $body | ConvertFrom-Json } catch { } }

    [pscustomobject]@{
        Code       = [int]$code
        Body       = $body
        Headers    = $headers
        Json       = $parsed
        Elapsed    = $watch.Elapsed
    }
}

function CountPdfs {
    if (-not (Test-Path $reportsDir)) { return 0 }
    @(Get-ChildItem $reportsDir -Filter *.pdf -ErrorAction SilentlyContinue).Count
}

# ---------------------------------------------------------------------------
# Start the API
# ---------------------------------------------------------------------------
Write-Host "Building..." -ForegroundColor DarkGray
& dotnet build -v quiet --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $here "bin\Debug\net9.0\PdfReportGenerator.dll"
if (-not (Test-Path $dll)) { throw "Not found: $dll" }

$env:ASPNETCORE_URLS        = $baseUrl
$env:ASPNETCORE_ENVIRONMENT = "Development"

$api = Start-Process -FilePath "dotnet" -ArgumentList "`"$dll`"" `
                     -WorkingDirectory $here -PassThru -WindowStyle Hidden `
                     -RedirectStandardOutput (Join-Path $here "api.out.log") `
                     -RedirectStandardError  (Join-Path $here "api.err.log")

try {
    Write-Host "Waiting for $baseUrl/health ..." -ForegroundColor DarkGray
    $up = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try { if ((Send "GET" "/health" $null).Code -eq 200) { $up = $true; break } } catch { }
    }
    if (-not $up) {
        Get-Content (Join-Path $here "api.err.log") -Raw -ErrorAction SilentlyContinue | Write-Host
        throw "API never became healthy."
    }

    # -----------------------------------------------------------------------
    Section "A - the server answers"
    $health = Send "GET" "/health" $null
    Check "GET /health returns 200" ($health.Code -eq 200) "got $($health.Code)"
    Check "GET /health says status=ok" ($health.Json.status -eq "ok") $health.Body

    # -----------------------------------------------------------------------
    Section "B - POST /reports runs the whole pipeline (Stage 4)"
    # force:true so this is deterministically a CREATE, whatever ran earlier today.
    $before = CountPdfs
    $create = Send "POST" "/reports" '{"force": true}'
    $after  = CountPdfs
    $id     = $create.Json.id

    Check "POST /reports returns 201 Created" ($create.Code -eq 201) "got $($create.Code): $($create.Body)"
    Check "the response carries an id" ($null -ne $id) $create.Body
    Check "the response carries a file LINK, not file bytes" `
          ($create.Json.file -eq "/reports/$id/file") $create.Body
    Check "the JSON body is small (a link, not a PDF)" `
          ($create.Body.Length -lt 500) "body was $($create.Body.Length) bytes"
    Check "a Location header points at the new report" `
          ($create.Headers -match "(?i)location:\s*/reports/$id") "no Location header"
    Check "exactly one new PDF appeared on disk" (($after - $before) -eq 1) "before=$before after=$after"
    # The point of Stage 4: the request is SLOW because the work is inside it.
    Check "the request visibly blocked (>1s)" ($create.Elapsed.TotalSeconds -gt 1.0) `
          ("took " + [math]::Round($create.Elapsed.TotalSeconds, 2) + "s")
    Write-Host ("        generation took " + [math]::Round($create.Elapsed.TotalSeconds, 2) + "s") -ForegroundColor DarkGray

    # -----------------------------------------------------------------------
    Section "C - GET /reports/{id} (Stage 4)"
    $record = Send "GET" "/reports/$id" $null
    Check "returns 200 for a real id" ($record.Code -eq 200) "got $($record.Code)"
    Check "returns the same id" ($record.Json.id -eq $id) $record.Body
    Check "has a createdAt timestamp" ($null -ne $record.Json.createdAt) $record.Body

    $missing = Send "GET" "/reports/999999" $null
    Check "unknown id returns 404" ($missing.Code -eq 404) "got $($missing.Code)"

    # -----------------------------------------------------------------------
    Section "D - GET /reports/{id}/file serves the bytes (store and link)"
    $download = Join-Path $env:TEMP "be08-download.pdf"
    $fileCode = & curl.exe -s -o $download -w "%{http_code}" "$baseUrl/reports/$id/file"
    $fileType = & curl.exe -s -o NUL -w "%{content_type}" "$baseUrl/reports/$id/file"

    Check "downloading the file returns 200" ([int]$fileCode -eq 200) "got $fileCode"
    Check "served as application/pdf" ($fileType -like "application/pdf*") "got $fileType"

    $head = [System.IO.File]::ReadAllBytes($download)[0..3]
    $magic = -join ($head | ForEach-Object { [char]$_ })
    Check "the downloaded file really is a PDF" ($magic -eq "%PDF") "starts with '$magic'"
    Check "the file is substantial (>10 KB)" ((Get-Item $download).Length -gt 10KB) `
          ((Get-Item $download).Length.ToString() + " bytes")
    Remove-Item $download -Force -ErrorAction SilentlyContinue

    # -----------------------------------------------------------------------
    Section "E - ask twice, get one (Stage 5)"
    $before2 = CountPdfs
    $first   = Send "POST" "/reports" $null      # no body at all, like curl -X POST
    $second  = Send "POST" "/reports" $null      # the double-click
    $after2  = CountPdfs

    Check "the first repeat POST returns 200, not 201" ($first.Code -eq 200) "got $($first.Code)"
    Check "the second repeat POST returns 200, not 201" ($second.Code -eq 200) "got $($second.Code)"
    # Guard against the vacuous pass: when both requests errored, both ids were
    # null, and "null equals null" reported success for a broken endpoint.
    Check "both answers actually carry an id" `
          (($null -ne $first.Json.id) -and ($null -ne $second.Json.id)) `
          "first=$($first.Body) second=$($second.Body)"
    Check "both answers carry the SAME id" `
          (($null -ne $first.Json.id) -and ($first.Json.id -eq $second.Json.id)) `
          "$($first.Json.id) vs $($second.Json.id)"
    Check "that id is today's existing report" ($first.Json.id -eq $id) `
          "$($first.Json.id) vs $id"
    Check "NO new file was written" (($after2 - $before2) -eq 0) "before=$before2 after=$after2"
    Check "a repeat is fast - no pipeline ran" ($second.Elapsed.TotalSeconds -lt 1.0) `
          ("took " + [math]::Round($second.Elapsed.TotalSeconds, 2) + "s")

    # -----------------------------------------------------------------------
    Section "F - force:true still generates a fresh one"
    $before3 = CountPdfs
    $forced  = Send "POST" "/reports" '{"force": true}'
    $after3  = CountPdfs

    Check "force returns 201 Created" ($forced.Code -eq 201) "got $($forced.Code)"
    Check "force produced a DIFFERENT id" ($forced.Json.id -ne $id) "$($forced.Json.id) vs $id"
    Check "force wrote exactly one new file" (($after3 - $before3) -eq 1) "before=$before3 after=$after3"

    # -----------------------------------------------------------------------
    Section "G - bad input never becomes a report"
    $before4 = CountPdfs
    $garbage = Send "POST" "/reports" 'not json at all'
    $after4  = CountPdfs
    Check "malformed JSON returns 400" ($garbage.Code -eq 400) "got $($garbage.Code): $($garbage.Body)"
    Check "and wrote no file" (($after4 - $before4) -eq 0) "before=$before4 after=$after4"
}
finally {
    if ($api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
$colour = if ($failed -eq 0) { "Green" } else { "Red" }
Write-Host ("$passed passed, $failed failed") -ForegroundColor $colour
if ($failed -gt 0) { exit 1 }
