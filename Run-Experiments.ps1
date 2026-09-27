<#
.SYNOPSIS
    Measures the startup flash of a dark WPF or WinUI 3 window for a set of startup variants.

.DESCRIPTION
    Builds FlashProbe, FlashLab and DarkStartupMinimal (Release; WinUiFlashLab too when a mode needs it)
    and starts every mode -Runs times. The
    modes are interleaved, so a slow drift of the machine state (GPU clocks, background load) affects all
    of them alike. FlashProbe records every frame DWM composes and appends one line per start to
    results/<Name>/summary.csv; per-run frame and event files go to results/<Name>/runs. The per-mode table
    is printed and written to results/<Name>/aggregate.csv, the machine description to environment.txt. A
    -Name whose folder already has a summary.csv is refused; a mode listed twice runs once.

    Modes are FlashLab modes ("baseline", "cloak=cr", "software+cloak=cr", ...; see README.md). Modes that
    start with "minimal" run the minimal repro itself; each "+flag" becomes "--flag", so
    "minimal+no-activate+no-cloak" runs DarkStartupMinimal --no-activate --no-cloak. Modes that start with
    "winui" run WinUiFlashLab with the rest of the mode: "winui" runs its baseline, "winui+cloak=rendered"
    runs WinUiFlashLab cloak=rendered.

    Windows open in the middle of the primary monitor, over a grey backdrop. Leave the machine alone while
    this runs; one start takes about 4 seconds.

.EXAMPLE
    pwsh ./Run-Experiments.ps1 -Runs 5

.EXAMPLE
    pwsh ./Run-Experiments.ps1 -Runs 20 -Modes minimal, minimal+no-cloak -Name fix-only
#>
[CmdletBinding()]
param(
    [int] $Runs = 5,
    [string[]] $Modes = @(
        'minimal+no-cloak', 'minimal', 'minimal+no-activate+no-cloak', 'minimal+no-activate',
        'plain', 'mica', 'software', 'nofade', 'erase', 'sync', 'noime',
        'cloak=loaded', 'cloak=erase+erase', 'cloak=tick', 'cloak=opdone', 'cloak=cr+onshow', 'cloak=cr+rgn',
        'cloak=cr', 'noactivate+cloak=cr', 'noactivate+cloak=active', 'noactivate+cloak=showpos',
        'software+cloak=cr+onactivate', 'mica+cloak=cr+onactivate',
        'cloak=frames', 'cloak=complete+inval', 'cloak=late',
        'heavy', 'cloak=cr+onactivate+heavy', 'reshow'),
    [string] $Name = (Get-Date -Format 'yyyy-MM-dd-HHmm'),
    [double] $AfterMs = 1500,
    [switch] $NoBackdrop
)

$ErrorActionPreference = 'Stop'
[System.Threading.Thread]::CurrentThread.CurrentCulture = [cultureinfo]::InvariantCulture
# "pwsh -File" passes "-Modes a,b" as one string.
# A mode listed twice would run twice per round under the same label and be counted twice.
$Modes = @($Modes | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim(" '`"") } | Where-Object { $_ } | Select-Object -Unique)
$root = $PSScriptRoot
$out = Join-Path $root "results/$Name"
$runsDir = Join-Path $out 'runs'
# FlashProbe appends to summary.csv, so a second run into the same folder would mix with the first one.
if (Test-Path (Join-Path $out 'summary.csv')) { throw "results/$Name already has a summary.csv; pick another -Name." }
New-Item -ItemType Directory -Force -Path $runsDir | Out-Null

$projects = @('FlashProbe/FlashProbe.csproj', 'FlashLab/FlashLab.csproj', 'DarkStartupMinimal.csproj')
if ($Modes -like 'winui*') { $projects += 'WinUiFlashLab/WinUiFlashLab.csproj' }
foreach ($project in $projects) {
    $log = dotnet build (Join-Path $root $project) -c Release -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw "Build failed: $project" }
}
$probe = Join-Path $root 'FlashProbe/bin/Release/net10.0-windows/FlashProbe.exe'
$lab = Join-Path $root 'FlashLab/bin/Release/net10.0-windows10.0.19041.0/FlashLab.exe'
$minimal = Join-Path $root 'bin/Release/net10.0-windows10.0.19041.0/DarkStartupMinimal.exe'
$winui = Join-Path $root 'WinUiFlashLab/bin/Release/net10.0-windows10.0.19041.0/win-x64/WinUiFlashLab.exe'

# What the numbers depend on: OS build, GPUs, refresh rates, animation and theme settings.
$os = Get-CimInstance Win32_OperatingSystem
$environment = @(
    "Date: $(Get-Date -Format 'yyyy-MM-dd')"
    "OS: $($os.Caption) $($os.Version)"
    ".NET SDK: $(dotnet --version)"
    (dotnet --list-runtimes | Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 10.*' } | ForEach-Object { "Runtime: " + ($_ -replace '\s*\[.*\]$', '') })
    if ($Modes -like 'winui*') {
        (Get-AppxPackage 'Microsoft.WindowsAppRuntime.2*' | Where-Object Architecture -EQ X64 | Sort-Object { [version]$_.Version } | Select-Object -Last 1 |
            ForEach-Object { "Windows App Runtime: $($_.Name) $($_.Version)" })
        "Windows App SDK package: " + ([xml](Get-Content (Join-Path $root 'WinUiFlashLab/WinUiFlashLab.csproj'))).Project.ItemGroup.PackageReference.Version
    }
    (Get-CimInstance Win32_VideoController | ForEach-Object {
        "GPU: $($_.Name) (driver $($_.DriverVersion)), $($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution) @ $($_.CurrentRefreshRate) Hz" })
    "Window animations (MinAnimate): $((Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name MinAnimate -ErrorAction SilentlyContinue).MinAnimate)"
    "AppsUseLightTheme: $((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme)"
    "Runs per mode: $Runs, recorded after WS_VISIBLE: $AfterMs ms, backdrop: $(-not $NoBackdrop)"
)
$environment | Set-Content (Join-Path $out 'environment.txt')
$environment | ForEach-Object { Write-Host $_ }

$summary = Join-Path $out 'summary.csv'
for ($run = 1; $run -le $Runs; $run++) {
    foreach ($mode in $Modes) {
        if ($mode -like 'minimal*') {
            # minimal+no-activate+no-cloak -> DarkStartupMinimal --no-activate --no-cloak
            $target = $minimal
            $childArgs = (($mode -split '\+') | Select-Object -Skip 1 | ForEach-Object { "--$_" }) -join ' '
        }
        elseif ($mode -like 'winui*') {
            # winui -> WinUiFlashLab baseline; winui+cloak=rendered -> WinUiFlashLab cloak=rendered
            $target = $winui
            $childArgs = if ($mode -eq 'winui') { 'baseline' } else { $mode.Substring('winui+'.Length) }
        }
        else {
            $target = $lab
            $childArgs = $mode
        }
        $probeArgs = @($target, '--out', $runsDir, '--label', "$mode~$run", '--summary', $summary, '--after', "$AfterMs")
        if ($childArgs) { $probeArgs += @('--args', $childArgs) }
        if (-not $NoBackdrop) { $probeArgs += '--backdrop' }
        $line = & $probe @probeArgs | Where-Object { $_ -like '`[*' } | Select-Object -First 1
        Write-Host $line
        Start-Sleep -Milliseconds 300
    }
}

# Per mode: how many starts showed a flash (light or dark), how many never showed the window, how fast it appeared. Starts in
# which another window covered the target (someone used the machine) are counted apart and left out.
function Median([double[]] $values) {
    if ($values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

$rows = Import-Csv $summary | ForEach-Object { $_ | Add-Member -NotePropertyName mode -NotePropertyValue ($_.label -replace '~\d+$', '') -PassThru }
$aggregate = foreach ($mode in $Modes) {
    $all = @($rows | Where-Object mode -EQ $mode)
    if ($all.Count -eq 0) { continue }
    $group = @($all | Where-Object { [int]$_.covered_frames -eq 0 })
    $shown = @($group | Where-Object shown_ms -NE '' | ForEach-Object { [double]$_.shown_ms })
    [pscustomobject]@{
        mode = $mode
        runs = $group.Count
        covered_runs = $all.Count - $group.Count
        flash_runs = @($group | Where-Object { [int]$_.flash_frames -gt 0 }).Count
        dark_flash_runs = @($group | Where-Object { $_.dark_flash_frames -and [int]$_.dark_flash_frames -gt 0 }).Count
        never_shown = @($group | Where-Object shown_ms -EQ '').Count
        median_shown_ms = Median $shown
        max_peak = ($group | ForEach-Object { [double]$_.peak } | Measure-Object -Maximum).Maximum
        min_darkest = ($group | Where-Object darkest | ForEach-Object { [double]$_.darkest } | Measure-Object -Minimum).Minimum
        behind = Median @($group | ForEach-Object { [double]$_.behind })
    }
}
$aggregate | Export-Csv (Join-Path $out 'aggregate.csv') -NoTypeInformation
$aggregate | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Results: $out"
