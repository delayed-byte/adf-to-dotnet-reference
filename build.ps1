#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Entry point for the ADF-to-.NET reference implementation.
.EXAMPLE
    ./build.ps1 up      # start Db2, SQL Server, Azurite, WireMock, Aspire dashboard
    ./build.ps1 build   # dotnet build, warnings as errors
    ./build.ps1 seed    # load synthetic data (implemented in Phase 1/2)
    ./build.ps1 test    # run the test suite
    ./build.ps1 run     # start the Functions host (requires Azure Functions Core Tools)
    ./build.ps1 scan    # gitleaks secret scan (via Docker)
    ./build.ps1 down    # stop containers (add -Volumes to delete data)
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'down', 'build', 'seed', 'test', 'run', 'scan')]
    [string]$Command = 'build',
    [switch]$Volumes
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function New-Password {
    # Meets SQL Server and Db2 complexity rules; random per machine, never committed.
    $bytes = [byte[]]::new(18)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    'Aa1-' + ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', 'x')
}

function Initialize-EnvFile {
    if (Test-Path .env) { return }
    @("DB2_PASSWORD=$(New-Password)", "SQL_PASSWORD=$(New-Password)") | Set-Content .env -Encoding ascii
    Write-Host 'Generated .env with random container passwords (git-ignored).'
}

function Wait-Port {
    param([int]$Port, [int]$TimeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-NetConnection -ComputerName localhost -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue) { return }
        Start-Sleep -Seconds 2
    }
    throw "Port $Port did not open within $TimeoutSeconds seconds."
}

switch ($Command) {
    'up' {
        Initialize-EnvFile
        # --wait blocks until every service with a healthcheck is healthy (Db2 can take 1-3 minutes).
        Invoke-Native docker @('compose', 'up', '-d', '--wait', '--wait-timeout', '600')
        Wait-Port -Port 18888
        Invoke-Native docker @('compose', 'ps')
    }
    'down' {
        $composeArgs = @('compose', 'down')
        if ($Volumes) { $composeArgs += '-v' }
        Invoke-Native docker $composeArgs
    }
    'build' {
        Invoke-Native dotnet @('build', 'AdfToDotnet.sln', '--nologo')
    }
    'seed' {
        Write-Host 'Seeding is implemented in Phase 1 (schemas) and Phase 2 (data generator).'
    }
    'test' {
        Invoke-Native dotnet @('test', '--solution', 'AdfToDotnet.sln', '--nologo')
    }
    'run' {
        if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
            throw 'Azure Functions Core Tools not found. Install with: winget install Microsoft.Azure.FunctionsCoreTools'
        }
        Invoke-Native dotnet @('build', 'src/CreditUnion.Batch.Functions', '--nologo')
        Push-Location src/CreditUnion.Batch.Functions
        try { Invoke-Native func @('start') } finally { Pop-Location }
    }
    'scan' {
        Invoke-Native docker @('run', '--rm', '-v', "${PSScriptRoot}:/repo", 'zricethezav/gitleaks:v8.21.2', 'detect', '--source', '/repo', '--no-git', '--redact')
    }
}
