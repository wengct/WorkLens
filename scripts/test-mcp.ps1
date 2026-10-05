param([string]$Url = 'http://127.0.0.1:15077/mcp')
$ErrorActionPreference = 'Stop'
# Run ONLY against an isolated test instance: this adds a work entry and summary.
$headers = @{ Accept = 'application/json, text/event-stream'; 'MCP-Protocol-Version' = '2025-11-25' }
function Call-Mcp($Method, $Parameters) {
    $body = @{ jsonrpc = '2.0'; id = 1; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 10 -Compress
    $response = Invoke-WebRequest -Uri $Url -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    $text = [string]$response.Content
    if ($text.StartsWith('event:') -or $text.StartsWith('data:')) {
        $text = ($text -split "`n" | Where-Object { $_.StartsWith('data:') } | Select-Object -Last 1).Substring(5).Trim()
    }
    $result = $text | ConvertFrom-Json
    if ($result.error) { throw 'MCP protocol error' }
    return $result.result
}
function Call-Tool($Name, $Arguments) {
    $result = Call-Mcp 'tools/call' @{ name = $Name; arguments = $Arguments }
    if ([bool]$result.isError -eq [bool]$result.structuredContent.success) { throw 'Incorrect MCP error flag' }
    if ($result.content[0].text -ne $result.structuredContent.message) { throw 'Expected a brief text explanation' }
    return $result.structuredContent
}
$null = Call-Mcp 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'isolated-smoke'; version = '1' } }
$tools = Call-Mcp 'tools/list' @{}
if ($tools.tools.Count -ne 3) { throw 'Expected exactly three tools' }
$date = '2001-02-03'
$entry = Call-Tool 'worklens_create_work_entry' @{ date = $date; hours = 1; content = 'MCP isolated smoke test' }
if (!$entry.success) { throw 'Entry failed' }
$context = Call-Tool 'worklens_get_daily_context' @{ date = $date }
if (!$context.success) { throw 'Context failed' }
$summary = Call-Tool 'worklens_save_daily_summary' @{ date = $date; body = 'MCP smoke summary'; expectedVersion = $context.data.daily.expectedVersion }
if (!$summary.success) { throw 'Summary failed' }
$conflict = Call-Tool 'worklens_save_daily_summary' @{ date = $date; body = 'Stale overwrite'; expectedVersion = $null }
if ($conflict.code -ne 'conflict') { throw 'Expected version conflict' }
foreach ($guard in @(@{ Host = 'evil.example' }, @{ Origin = 'https://evil.example' })) {
    try {
        $null = Invoke-WebRequest -Uri $Url -Method Post -Headers $guard -ContentType 'application/json' -Body '{}'
        throw 'Guard allowed an invalid request'
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 403) { throw }
    }
}
Write-Output 'PASS: three tools, no-token HTTP writes, scanned context, version conflict, Host/Origin guards.'
