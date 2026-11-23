<#
.SYNOPSIS
    Runs all of KanbanHub on this machine and opens it in the browser.

.DESCRIPTION
    Starts the dev host (the real API on LocalDB, with local stand-ins for SignalR, Service Bus, GitHub, agents,
    AI and email) and the web app, waits until both answer, and stops both when you press Ctrl+C.

    It needs the .NET SDK and LocalDB only: no Azure account, no sign-in, and nothing that costs money.

.PARAMETER SqlConnectionString
    A SQL Server 2025 to use instead of LocalDB, for example one running in a container.

.PARAMETER NoBrowser
    Does not open the browser.

.EXAMPLE
    ./run-local.ps1
#>
[CmdletBinding()]
param(
    [string] $SqlConnectionString,
    [switch] $NoBrowser
)

$ErrorActionPreference = 'Stop'
$apiUrl = 'http://localhost:7071'
$webUrl = 'http://localhost:5173'

function Build-Project([string] $project) {
    dotnet build $project --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "$project did not build." }
}

# Paths are relative to the repository so that a folder name with a space needs no quoting.
function Start-Project([string] $project) {
    Start-Process dotnet -ArgumentList 'run', '--no-build', '--project', $project -NoNewWindow -PassThru
}

function Wait-Until([string] $url, [System.Diagnostics.Process] $process) {
    foreach ($attempt in 1..120) {
        if ($process.HasExited) { throw "The process serving $url stopped. Its output is above." }
        try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null; return }
        catch { Start-Sleep -Milliseconds 500 }
    }
    throw "$url did not answer within a minute."
}

# "dotnet run" starts the app as a child process, so the whole tree has to go.
function Stop-Tree($process) {
    if ($null -eq $process -or $process.HasExited) { return }
    if ($env:OS -eq 'Windows_NT') { taskkill /PID $process.Id /T /F | Out-Null }
    else { Stop-Process -Id $process.Id }
}

Push-Location $PSScriptRoot
try {
    Build-Project 'tools/KanbanHub.DevHost'
    Build-Project 'src/KanbanHub.Web'

    if ($SqlConnectionString) { $env:Sql__ConnectionString = $SqlConnectionString }
    $api = Start-Project 'tools/KanbanHub.DevHost'
    $web = Start-Project 'src/KanbanHub.Web'

    Wait-Until "$apiUrl/api/health" $api
    Wait-Until $webUrl $web

    Write-Host "`nKanbanHub is running at $webUrl. Press Ctrl+C to stop it.`n" -ForegroundColor Green
    if (-not $NoBrowser) { Start-Process $webUrl }

    Wait-Process -Id $api.Id, $web.Id
}
finally {
    Stop-Tree $api
    Stop-Tree $web
    Pop-Location
}
