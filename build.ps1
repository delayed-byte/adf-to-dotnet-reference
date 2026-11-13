#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Entry point for the ADF-to-.NET reference implementation.
.EXAMPLE
    ./build.ps1 up      # start Db2, SQL Server, Azurite, WireMock, Aspire dashboard
    ./build.ps1 build   # dotnet build, warnings as errors
    ./build.ps1 seed    # deploy the Db2 and SQL Server schemas (DbUp); requires 'up'
    ./build.ps1 test    # run the test suite (Category=Performance excluded; add -Performance to run only those)
    ./build.ps1 golden  # regenerate tests/TestData inputs and golden outputs (add -Scenario NAME for one)
    ./build.ps1 run     # start the Functions host (requires Azure Functions Core Tools)
    ./build.ps1 infra   # compile and lint the Azure Bicep template (needs the Bicep CLI)
    ./build.ps1 scan    # gitleaks secret scan (via Docker)
    ./build.ps1 down    # stop containers (add -Volumes to delete data)
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'down', 'build', 'seed', 'test', 'golden', 'run', 'infra', 'scan')]
    [string]$Command = 'build',
    [switch]$Volumes,
    [switch]$Performance,
    [string[]]$Scenario = @()
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

$EnvKeys = @('DB2_PASSWORD', 'DB2_EXTRACT_PASSWORD', 'SQL_PASSWORD', 'SQL_WRITER_PASSWORD', 'SQL_READER_PASSWORD')

function Read-EnvFile {
    $values = @{}
    if (Test-Path .env) {
        foreach ($line in Get-Content .env) {
            if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
        }
    }
    $values
}

function Initialize-EnvFile {
    # Adds any missing password to .env (git-ignored); existing values are kept.
    $values = Read-EnvFile
    $missing = $EnvKeys | Where-Object { -not $values.ContainsKey($_) }
    if (-not $missing) { return }
    $missing | ForEach-Object { "$_=$(New-Password)" } | Add-Content .env -Encoding ascii
    Write-Host "Generated random values in .env for: $($missing -join ', ')"
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
        # Deploys both database schemas (DbUp). Scenario data is loaded by the test harness.
        Initialize-EnvFile
        $envValues = Read-EnvFile
        $env:KEYSTONE_ADMIN_CONNECTION = "Server=localhost:50000;Database=KEYSTONE;UID=db2inst1;PWD=$($envValues.DB2_PASSWORD);"
        $env:REPORTING_ADMIN_CONNECTION = "Server=localhost,14330;Database=Reporting;User Id=sa;Password=$($envValues.SQL_PASSWORD);Encrypt=True;TrustServerCertificate=True;"
        $env:REPORTING_WRITER_PASSWORD = $envValues.SQL_WRITER_PASSWORD
        $env:REPORTING_READER_PASSWORD = $envValues.SQL_READER_PASSWORD
        try {
            Invoke-Native dotnet @('run', '--project', 'tools/CreditUnion.Batch.DbDeploy', '--', 'all')
        }
        finally {
            Remove-Item Env:KEYSTONE_ADMIN_CONNECTION, Env:REPORTING_ADMIN_CONNECTION, Env:REPORTING_WRITER_PASSWORD, Env:REPORTING_READER_PASSWORD -ErrorAction SilentlyContinue
        }
    }
    'test' {
        # Testcontainers starts its own Db2, SQL Server and Azurite (first Db2 start: 1-3 minutes).
        # Set CU_TESTS_USE_COMPOSE=1 to reuse the './build.ps1 up' stack instead.
        $filter = if ($Performance) { @('--filter-trait', 'Category=Performance') } else { @('--filter-not-trait', 'Category=Performance') }
        Invoke-Native dotnet (@('test', '--solution', 'AdfToDotnet.sln', '--nologo', '--') + $filter)
    }
    'golden' {
        # Golden outputs change only through the generator; review the diff (tests/TestData/REVIEW.md).
        $generatorArgs = @('run', '--project', 'tests/TestData/generator', '--')
        foreach ($name in $Scenario) { $generatorArgs += @('--scenario', $name) }
        Invoke-Native dotnet $generatorArgs
    }
    'run' {
        if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
            throw 'Azure Functions Core Tools not found. Install with: winget install Microsoft.Azure.FunctionsCoreTools'
        }
        Invoke-Native dotnet @('build', 'src/CreditUnion.Batch.Functions', '--nologo')
        Push-Location src/CreditUnion.Batch.Functions
        try { Invoke-Native func @('start') } finally { Pop-Location }
    }
    'infra' {
        # Compiles and lints the Azure template offline (ADR 0020); no subscription needed.
        # Needs the standalone Bicep CLI on PATH; az bicep takes different arguments and may stop to prompt.
        $bicep = (Get-Command bicep -ErrorAction SilentlyContinue).Source
        if (-not $bicep) {
            $azBicep = Join-Path $HOME '.azure/bin/bicep.exe'
            if (-not (Test-Path $azBicep)) {
                throw 'Bicep CLI not found. Install with: winget install Microsoft.Bicep (or az bicep install)'
            }
            $bicep = $azBicep
        }
        $out = Join-Path ([System.IO.Path]::GetTempPath()) 'cu-batch-bicep'
        New-Item -ItemType Directory -Force $out | Out-Null
        Invoke-Native $bicep @('build', 'infra/main.bicep', '--outdir', $out)
        Invoke-Native $bicep @('lint', 'infra/main.bicep')
        Invoke-Native $bicep @('build-params', 'infra/main.prod.bicepparam', '--outfile', (Join-Path $out 'main.prod.parameters.json'))
        Write-Host 'infra/main.bicep compiles and lints clean.'
    }
    'scan' {
        Invoke-Native docker @('run', '--rm', '-v', "${PSScriptRoot}:/repo", 'zricethezav/gitleaks:v8.21.2', 'detect', '--source', '/repo', '--no-git', '--redact')
    }
}
