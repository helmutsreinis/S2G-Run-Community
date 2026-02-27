<#
.SYNOPSIS
    S2G Run Remote Client for Windows
.DESCRIPTION
    Polls the HttpListener proxy for commands and executes them on the local machine.
    Reports system metrics back to the workflow.
.NOTES
    Requires PowerShell 5.0 or later
#>

# ============================================
# CONFIGURATION - EDIT THESE VALUES
# ============================================
$ProxyUrl = "__PLACEHOLDER_PROXY_URL__"
$ListenerNodeId = "__PLACEHOLDER_LISTENER_ID__"  # Set your HttpListener node ID here
$ClientId = "__PLACEHOLDER_CLIENT_ID__"        # Set your Remote node ClientId here

# Polling intervals
$IdlePollInterval = 60      # Seconds between polls in idle mode
$ActivePollInterval = 15    # Seconds between polls after command execution
$ActiveDuration = 60        # Seconds to stay in active mode
$DefaultTimeout = 60        # Default command timeout in seconds

# Custom Reports
# Get script directory robustly (works in all execution contexts)
$ScriptDir = if ($PSScriptRoot) { 
    $PSScriptRoot 
}
elseif ($MyInvocation.MyCommand.Path) { 
    Split-Path -Parent $MyInvocation.MyCommand.Path 
}
else { 
    $PWD.Path 
}
$ReportsDir = Join-Path $ScriptDir "modal_reports"
$ReportsConfigFile = Join-Path $ReportsDir "_config.json"

# ============================================
# FUNCTIONS
# ============================================

function Get-SystemMetrics {
    <#
    .SYNOPSIS
        Collect system metrics for reporting, including multi-disk breakdown
    #>
    try {
        $cpu = (Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction SilentlyContinue).CounterSamples[0].CookedValue
        $memory = (Get-Counter '\Memory\% Committed Bytes In Use' -ErrorAction SilentlyContinue).CounterSamples[0].CookedValue
        
        # Get all fixed drives with proper percentage calculation
        $drives = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -gt 0 -and $_.Free -ge 0 }
        $diskBreakdown = @()
        $totalUsed = 0
        $totalSize = 0
        
        foreach ($drive in $drives) {
            $driveSize = $drive.Used + $drive.Free
            if ($driveSize -gt 0) {
                $usedPercent = [math]::Round(($drive.Used / $driveSize) * 100, 1)
                $diskBreakdown += @{
                    drive       = "$($drive.Name):"
                    usedPercent = $usedPercent
                    usedGB      = [math]::Round($drive.Used / 1GB, 1)
                    freeGB      = [math]::Round($drive.Free / 1GB, 1)
                    totalGB     = [math]::Round($driveSize / 1GB, 1)
                }
                $totalUsed += $drive.Used
                $totalSize += $driveSize
            }
        }
        
        # Calculate overall disk usage percentage
        $disk = if ($totalSize -gt 0) { [math]::Round(($totalUsed / $totalSize) * 100, 1) } else { 0 }
    }
    catch {
        # Fallback using WMI
        $cpu = (Get-WmiObject Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
        $os = Get-WmiObject Win32_OperatingSystem
        $memory = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize * 100, 1)
        
        # Get all disks via WMI
        $wmiDisks = Get-WmiObject Win32_LogicalDisk -Filter "DriveType=3"
        $diskBreakdown = @()
        $totalUsed = 0
        $totalSize = 0
        
        foreach ($wmiDisk in $wmiDisks) {
            if ($wmiDisk.Size -gt 0) {
                $used = $wmiDisk.Size - $wmiDisk.FreeSpace
                $usedPercent = [math]::Round(($used / $wmiDisk.Size) * 100, 1)
                $diskBreakdown += @{
                    drive       = $wmiDisk.DeviceID
                    usedPercent = $usedPercent
                    usedGB      = [math]::Round($used / 1GB, 1)
                    freeGB      = [math]::Round($wmiDisk.FreeSpace / 1GB, 1)
                    totalGB     = [math]::Round($wmiDisk.Size / 1GB, 1)
                }
                $totalUsed += $used
                $totalSize += $wmiDisk.Size
            }
        }
        
        $disk = if ($totalSize -gt 0) { [math]::Round(($totalUsed / $totalSize) * 100, 1) } else { 0 }
    }
    
    return @{
        hostname      = $env:COMPUTERNAME
        os            = "$([System.Environment]::OSVersion.Platform) $([System.Environment]::OSVersion.Version)"
        cpu           = [math]::Round($cpu, 1)
        memory        = [math]::Round($memory, 1)
        disk          = $disk
        diskBreakdown = ($diskBreakdown | ConvertTo-Json -Compress)
    }
}

function Invoke-FetchCommands {
    <#
    .SYNOPSIS
        Fetch pending commands from the Remote node via HttpListener proxy
    #>
    try {
        $body = @{
            action   = "fetch"
            clientId = $ClientId
        } | ConvertTo-Json
        
        $response = Invoke-RestMethod -Uri "$ProxyUrl`?nodeId=$ListenerNodeId" `
            -Method Post `
            -ContentType "application/json" `
            -Body $body `
            -TimeoutSec 30
        
        # Debug: Show what we received
        Write-Host "[DEBUG] Response type: $($response.GetType().Name), Count: $(@($response).Count)" -ForegroundColor DarkGray
        if ($response) {
            Write-Host "[DEBUG] First item: $($response | Select-Object -First 1 | ConvertTo-Json -Compress)" -ForegroundColor DarkGray
        }
        
        # Handle various response formats
        if ($null -eq $response) {
            return @()
        }
        
        # If response is already an array, return it (use comma to prevent unwrapping)
        if ($response -is [array]) {
            return , $response
        }
        
        # If response is a single PSCustomObject with command properties, wrap it
        if ($response.executionId -and $response.command) {
            return @($response)
        }
        
        # If response is a string (JSON), parse it
        if ($response -is [string]) {
            if ($response.Trim().StartsWith('[') -or $response.Trim().StartsWith('{')) {
                $parsed = $response | ConvertFrom-Json
                if ($parsed -is [array]) {
                    return $parsed
                }
                elseif ($parsed.executionId) {
                    return @($parsed)
                }
            }
            return @()
        }
        
        # If response has a body property (wrapped response)
        if ($response.body) {
            if ($response.body -is [string]) {
                $parsed = $response.body | ConvertFrom-Json
                if ($parsed -is [array]) {
                    return $parsed
                }
                return @($parsed)
            }
            return @($response.body)
        }
        
        return @()
    }
    catch {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Fetch error: $_" -ForegroundColor Red
        return @()
    }
}

function Invoke-Command-WithTimeout {
    param(
        [string]$Command,
        [int]$TimeoutSeconds = 60
    )
    
    <#
    .SYNOPSIS
        Execute a shell command with timeout, capturing both stdout and stderr.
        Supports Base64-encoded PowerShell commands with __PS64__ prefix.
    #>
    try {
        # Check if this is a Base64-encoded PowerShell command
        $isPowerShell = $Command.StartsWith("__PS64__")
        $actualCommand = $Command
        
        if ($isPowerShell) {
            # Decode the Base64 command
            $base64Part = $Command.Substring(8)  # Remove __PS64__ prefix
            $actualCommand = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64Part))
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 🔓 Decoded PowerShell command: $($actualCommand.Substring(0, [Math]::Min(80, $actualCommand.Length)))..." -ForegroundColor DarkCyan
        }
        
        $job = Start-Job -ScriptBlock {
            param($cmd, $usePowerShell)
            
            # Capture both stdout and stderr separately
            $stdout = $null
            $stderr = $null
            $exitCode = 0
            
            try {
                if ($usePowerShell) {
                    # Execute directly as PowerShell script block for proper cmdlet support
                    $process = New-Object System.Diagnostics.Process
                    $process.StartInfo.FileName = "powershell.exe"
                    $process.StartInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command `"$($cmd -replace '"', '\"')`""
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    $process.StartInfo.CreateNoWindow = $true
                    
                    $process.Start() | Out-Null
                    
                    $stdout = $process.StandardOutput.ReadToEnd()
                    $stderr = $process.StandardError.ReadToEnd()
                    
                    $process.WaitForExit()
                    $exitCode = $process.ExitCode
                }
                else {
                    # Use cmd /c for basic commands
                    $process = New-Object System.Diagnostics.Process
                    $process.StartInfo.FileName = "cmd.exe"
                    $process.StartInfo.Arguments = "/c $cmd"
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    $process.StartInfo.CreateNoWindow = $true
                    
                    $process.Start() | Out-Null
                    
                    $stdout = $process.StandardOutput.ReadToEnd()
                    $stderr = $process.StandardError.ReadToEnd()
                    
                    $process.WaitForExit()
                    $exitCode = $process.ExitCode
                }
            }
            catch {
                $stderr = "PowerShell error: $($_.Exception.Message)"
                $exitCode = -1
            }
            
            # Combine output - show errors prominently
            $combinedOutput = ""
            if ($stdout) { $combinedOutput += $stdout.TrimEnd() }
            if ($stderr) {
                if ($combinedOutput) { $combinedOutput += "`n`n--- STDERR ---`n" }
                $combinedOutput += $stderr.TrimEnd()
            }
            
            return @{
                Output   = $combinedOutput
                ExitCode = $exitCode
                HasError = [bool]$stderr
            }
        } -ArgumentList $actualCommand, $isPowerShell
        
        $completed = Wait-Job -Job $job -Timeout $TimeoutSeconds
        
        if ($completed) {
            $result = Receive-Job -Job $job
            Remove-Job -Job $job -Force
            
            # Show warning if there was stderr output
            if ($result.HasError) {
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ⚠️ Command produced error output" -ForegroundColor Yellow
            }
            
            return @{
                Output   = $result.Output
                ExitCode = if ($null -ne $result.ExitCode) { $result.ExitCode } else { 0 }
            }
        }
        else {
            Stop-Job -Job $job
            Remove-Job -Job $job -Force
            return @{
                Output   = "⏱️ Command timed out after $TimeoutSeconds seconds"
                ExitCode = -1
            }
        }
    }
    catch {
        return @{
            Output   = "❌ Execution error: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
            ExitCode = -1
        }
    }
}

function Submit-Output {
    param(
        [string]$ExecutionId,
        [string]$Output,
        [int]$ExitCode,
        [hashtable]$Metrics
    )
    
    <#
    .SYNOPSIS
        Submit command output and system metrics to the Remote node
    #>
    try {
        # Limit output size
        $trimmedOutput = if ($Output.Length -gt 50000) { $Output.Substring(0, 50000) } else { $Output }
        
        $body = @{
            action      = "submit"
            clientId    = $ClientId
            executionId = $ExecutionId
            output      = $trimmedOutput
            exitCode    = $ExitCode
            hostname    = $Metrics.hostname
            os          = $Metrics.os
            cpu         = $Metrics.cpu
            memory      = $Metrics.memory
            disk        = $Metrics.disk
        } | ConvertTo-Json
        
        $response = Invoke-RestMethod -Uri "$ProxyUrl`?nodeId=$ListenerNodeId" `
            -Method Post `
            -ContentType "application/json" `
            -Body $body `
            -TimeoutSec 30
        
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Submitted result for $($ExecutionId.Substring(0, 8))..." -ForegroundColor Green
    }
    catch {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Submit error: $_" -ForegroundColor Red
    }
}

# ============================================
# CUSTOM REPORTS FUNCTIONS
# ============================================

function Initialize-ReportsDirectory {
    <#
    .SYNOPSIS
        Ensure the modal_reports directory and config exist
    #>
    if (-not (Test-Path $ReportsDir)) {
        New-Item -Path $ReportsDir -ItemType Directory -Force | Out-Null
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 📁 Created modal_reports directory" -ForegroundColor Green
    }
    if (-not (Test-Path $ReportsConfigFile)) {
        @{ reports = @() } | ConvertTo-Json | Set-Content $ReportsConfigFile
    }
}

function Get-ReportsConfig {
    <#
    .SYNOPSIS
        Load the reports configuration
    #>
    try {
        if (Test-Path $ReportsConfigFile) {
            return Get-Content $ReportsConfigFile -Raw | ConvertFrom-Json
        }
    }
    catch {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ⚠️ Error loading reports config: $_" -ForegroundColor Yellow
    }
    return @{ reports = @() }
}

function Save-ReportsConfig {
    param([object]$Config)
    try {
        $Config | ConvertTo-Json -Depth 10 | Set-Content $ReportsConfigFile
    }
    catch {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ⚠️ Error saving reports config: $_" -ForegroundColor Yellow
    }
}

function Invoke-CustomReports {
    <#
    .SYNOPSIS
        Execute all enabled custom reports and return results
    #>
    $results = @()
    $config = Get-ReportsConfig
    $now = Get-Date
    
    foreach ($report in $config.reports) {
        if (-not $report.enabled) { continue }
        
        $reportName = $report.name
        $scriptPath = Join-Path $ReportsDir "$reportName.ps1"
        
        # Check if script file exists
        if (-not (Test-Path $scriptPath)) {
            $results += @{
                name        = $reportName
                enabled     = $false
                error       = "Script file not found: $reportName.ps1"
                data        = $null
                lastUpdated = $now.ToString("o")
            }
            continue
        }
        
        # Execute the report script with timeout
        try {
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 📊 Executing report: $reportName" -ForegroundColor Cyan
            
            $job = Start-Job -ScriptBlock {
                param($path)
                & $path
            } -ArgumentList $scriptPath
            
            $completed = Wait-Job -Job $job -Timeout 30
            
            if ($completed) {
                $output = Receive-Job -Job $job
                Remove-Job -Job $job -Force
                
                # Try to parse as JSON
                $jsonOutput = $null
                if ($output -is [string]) {
                    try { $jsonOutput = $output | ConvertFrom-Json } catch { }
                }
                elseif ($output -is [array]) {
                    $jsonOutput = $output
                }
                
                $results += @{
                    name        = $reportName
                    enabled     = $true
                    error       = $null
                    data        = if ($jsonOutput) { $jsonOutput } else { $output }
                    lastUpdated = $now.ToString("o")
                }
                
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✅ Report completed: $reportName" -ForegroundColor Green
            }
            else {
                Stop-Job -Job $job
                Remove-Job -Job $job -Force
                $results += @{
                    name        = $reportName
                    enabled     = $true
                    error       = "Report timed out after 30 seconds"
                    data        = $null
                    lastUpdated = $now.ToString("o")
                }
            }
        }
        catch {
            $results += @{
                name        = $reportName
                enabled     = $true
                error       = $_.Exception.Message
                data        = $null
                lastUpdated = $now.ToString("o")
            }
        }
    }
    
    return $results
}

function Handle-ReportCommand {
    param([object]$ReportCmd)
    <#
    .SYNOPSIS
        Handle report management commands from server (add/update/delete/toggle)
    #>
    $action = $ReportCmd.action
    $reportName = $ReportCmd.name
    $scriptContent = $ReportCmd.script
    $intervalSeconds = if ($ReportCmd.intervalSeconds) { $ReportCmd.intervalSeconds } else { 60 }
    
    $config = Get-ReportsConfig
    $scriptPath = Join-Path $ReportsDir "$reportName.ps1"
    
    switch ($action) {
        "add" {
            # Create/update the script file
            $scriptContent | Set-Content $scriptPath -Force
            
            # Update config
            $existing = $config.reports | Where-Object { $_.name -eq $reportName }
            if ($existing) {
                $existing.intervalSeconds = $intervalSeconds
                $existing.enabled = $true
            }
            else {
                $config.reports += @{
                    name            = $reportName
                    enabled         = $true
                    intervalSeconds = $intervalSeconds
                }
            }
            Save-ReportsConfig -Config $config
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ➕ Added report: $reportName" -ForegroundColor Green
        }
        "update" {
            if ($scriptContent) {
                $scriptContent | Set-Content $scriptPath -Force
            }
            $existing = $config.reports | Where-Object { $_.name -eq $reportName }
            if ($existing -and $intervalSeconds) {
                $existing.intervalSeconds = $intervalSeconds
            }
            Save-ReportsConfig -Config $config
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✏️ Updated report: $reportName" -ForegroundColor Green
        }
        "delete" {
            if (Test-Path $scriptPath) {
                Remove-Item $scriptPath -Force
            }
            $config.reports = @($config.reports | Where-Object { $_.name -ne $reportName })
            Save-ReportsConfig -Config $config
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 🗑️ Deleted report: $reportName" -ForegroundColor Yellow
        }
        "toggle" {
            $existing = $config.reports | Where-Object { $_.name -eq $reportName }
            if ($existing) {
                $existing.enabled = -not $existing.enabled
                Save-ReportsConfig -Config $config
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 🔄 Toggled report: $reportName (enabled=$($existing.enabled))" -ForegroundColor Cyan
            }
        }
    }
}

# ============================================
# MAIN LOOP
# ============================================

# Validate configuration
if ([string]::IsNullOrEmpty($ListenerNodeId) -or [string]::IsNullOrEmpty($ClientId)) {
    Write-Host "ERROR: Please configure ListenerNodeId and ClientId at the top of this script." -ForegroundColor Red
    Write-Host "  ListenerNodeId = Your HttpListener node ID from the workflow"
    Write-Host "  ClientId = Your Remote node ClientId from the node properties"
    exit 1
}

function Send-Heartbeat {
    <#
    .SYNOPSIS
        Send heartbeat with system metrics and custom reports
    #>
    try {
        $metrics = Get-SystemMetrics
        
        # Execute custom reports that are due
        $customReports = Invoke-CustomReports
        
        # Convert reports to JSON string - ensure it's always an array
        # Use @() to force array context and prevent single-element unwrapping
        $reportsJson = if ($customReports -and @($customReports).Count -gt 0) {
            # Wrap in @() to ensure array, then convert with -AsArray if available (PS 7+), else manually wrap
            $arr = @($customReports)
            $json = $arr | ConvertTo-Json -Depth 10 -Compress
            # If single element, PowerShell unwraps it - detect and fix
            if ($arr.Count -eq 1 -and -not $json.StartsWith('[')) {
                "[$json]"
            }
            else {
                $json
            }
        }
        else {
            "[]"
        }
        
        $body = @{
            action        = "heartbeat"
            clientId      = $ClientId
            hostname      = $metrics.hostname
            os            = $metrics.os
            cpu           = $metrics.cpu
            memory        = $metrics.memory
            disk          = $metrics.disk
            diskBreakdown = $metrics.diskBreakdown
            customReports = $reportsJson
        } | ConvertTo-Json -Depth 3
        
        Invoke-RestMethod -Uri "$ProxyUrl`?nodeId=$ListenerNodeId" `
            -Method Post `
            -ContentType "application/json" `
            -Body $body `
            -TimeoutSec 10 | Out-Null
        
        $reportCount = ($customReports | Where-Object { $_ }).Count
        $reportInfo = if ($reportCount -gt 0) { ", Reports: $reportCount" } else { "" }
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 💓 Heartbeat sent (CPU: $($metrics.cpu)%, Mem: $($metrics.memory)%, Disk: $($metrics.disk)%$reportInfo)" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Heartbeat error: $_" -ForegroundColor Red
    }
}

Write-Host "S2G Run Remote Client starting..." -ForegroundColor Cyan
Write-Host "  Listener Node: $($ListenerNodeId.Substring(0, 8))..."
Write-Host "  Client ID: $($ClientId.Substring(0, 8))..."
Write-Host "  Hostname: $env:COMPUTERNAME"
Write-Host "  OS: $([System.Environment]::OSVersion.VersionString)"
Write-Host ""

# Initialize custom reports directory
Initialize-ReportsDirectory
$reportsConfig = Get-ReportsConfig
$enabledReports = @($reportsConfig.reports | Where-Object { $_.enabled }).Count
if ($enabledReports -gt 0) {
    Write-Host "  📊 Custom Reports: $enabledReports enabled" -ForegroundColor Cyan
}
Write-Host ""

$lastCommandTime = $null
$lastHeartbeat = $null
$HeartbeatInterval = 30  # Send heartbeat every 30 seconds

while ($true) {
    try {
        # Send heartbeat every 30 seconds
        if (-not $lastHeartbeat -or ((Get-Date) - $lastHeartbeat).TotalSeconds -ge $HeartbeatInterval) {
            Send-Heartbeat
            $lastHeartbeat = Get-Date
        }
        
        # Determine poll interval based on mode
        if ($lastCommandTime -and ((Get-Date) - $lastCommandTime).TotalSeconds -lt $ActiveDuration) {
            $pollInterval = $ActivePollInterval
            $mode = "ACTIVE"
        }
        else {
            $pollInterval = $IdlePollInterval
            $mode = "IDLE"
        }
        
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] [$mode] Polling for commands..." -ForegroundColor Gray
        
        $commands = Invoke-FetchCommands
        
        if ($commands -and $commands.Count -gt 0) {
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Received $($commands.Count) command(s)" -ForegroundColor Cyan
            $lastCommandTime = Get-Date
            
            foreach ($cmd in $commands) {
                $executionId = $cmd.executionId
                $command = $cmd.command
                $timeout = if ($cmd.timeoutSeconds) { $cmd.timeoutSeconds } else { $DefaultTimeout }
                
                # Check if this is a report management command
                if ($command -and $command.StartsWith("__REPORT__")) {
                    try {
                        $reportJson = $command.Substring(10)  # Remove __REPORT__ prefix
                        $reportCmd = $reportJson | ConvertFrom-Json
                        Handle-ReportCommand -ReportCmd $reportCmd
                        
                        # Submit success response
                        Submit-Output -ExecutionId $executionId -Output "Report command executed successfully" -ExitCode 0 -Metrics (Get-SystemMetrics)
                    }
                    catch {
                        Submit-Output -ExecutionId $executionId -Output "Report command error: $_" -ExitCode 1 -Metrics (Get-SystemMetrics)
                    }
                    continue
                }
                
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Executing: $($command.Substring(0, [Math]::Min(50, $command.Length)))..." -ForegroundColor Yellow
                
                $result = Invoke-Command-WithTimeout -Command $command -TimeoutSeconds $timeout
                $metrics = Get-SystemMetrics
                
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Exit code: $($result.ExitCode)" -ForegroundColor $(if ($result.ExitCode -eq 0) { "Green" } else { "Red" })
                
                Submit-Output -ExecutionId $executionId -Output $result.Output -ExitCode $result.ExitCode -Metrics $metrics
            }
        }
        
        Start-Sleep -Seconds $pollInterval
    }
    catch {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Error: $_" -ForegroundColor Red
        Start-Sleep -Seconds $IdlePollInterval
    }
}

