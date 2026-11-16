param([Parameter(Mandatory = $true)][string]$AppPath, [string]$LaunchArguments = '--settings',
      [switch]$EnableGlobalHotkeys, [switch]$RestartTrial)
$ErrorActionPreference = 'Stop'
$resolvedTrialApp = (Resolve-Path -LiteralPath $AppPath).Path
if ((Split-Path -Leaf $resolvedTrialApp) -ne 'QingSnap.exe' -or
    $resolvedTrialApp -notmatch '[\\/]TranslationTrial[\\/]') {
    throw 'Only the separate TranslationTrial executable may be launched by this helper.'
}
$hasOptIn = $LaunchArguments -match '(?i)(?:^|\s)--enable-global-hotkeys(?:\s|$)'
if ($EnableGlobalHotkeys -and -not $hasOptIn) {
    $LaunchArguments = ($LaunchArguments + ' --enable-global-hotkeys').Trim()
    $hasOptIn = $true
}
$instances = @(Get-Process QingSnap -ErrorAction SilentlyContinue)
if ($hasOptIn -and @($instances | Where-Object { $_.Path -and $_.Path -notmatch '[\\/]TranslationTrial[\\/]' }).Count -gt 0) {
    throw 'The stable QingSnap is still running. Exit it before enabling trial global shortcuts; no stable process was stopped.'
}
$existingTrial = @($instances | Where-Object Path -EQ $resolvedTrialApp)
if ($existingTrial.Count -gt 0 -and -not $RestartTrial) {
    if ($hasOptIn) { throw 'Use -RestartTrial to change the hotkey mode of an existing trial process.' }
    $existingTrial | Select-Object Id, Path, Responding, PriorityClass, BasePriority
    return
}
$otherTrial = @($instances | Where-Object {
    $_.Path -match '[\\/]TranslationTrial[\\/]' -and $_.Path -ne $resolvedTrialApp
})
if ($otherTrial.Count -gt 0 -and -not $RestartTrial) { throw 'Save and exit the older translation trial before starting this build. The stable QingSnap process will not be stopped.' }

if ($RestartTrial) {
    Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class QingSnapTrialRestartNative {
 public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximum);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
 [DllImport("user32.dll", SetLastError=true)] public static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wp, IntPtr lp);
}
'@
    foreach ($trialInstance in @($instances | Where-Object { $_.Path -match '[\\/]TranslationTrial[\\/]' })) {
        $trialProcessId = [uint32]$trialInstance.Id
        $trialWindows = [System.Collections.Generic.List[object]]::new()
        [QingSnapTrialRestartNative]::EnumWindows({ param($windowHandle, $unused)
            $windowProcessId = [uint32]0
            $windowThreadId = [QingSnapTrialRestartNative]::GetWindowThreadProcessId($windowHandle, [ref]$windowProcessId)
            if ($windowProcessId -eq $trialProcessId) {
                $windowTitle = [System.Text.StringBuilder]::new(512)
                [void][QingSnapTrialRestartNative]::GetWindowText($windowHandle, $windowTitle, 512)
                $trialWindows.Add([pscustomobject]@{Handle=$windowHandle;ThreadId=$windowThreadId;
                    Host=($windowTitle.ToString() -eq 'QingSnap');Visible=[QingSnapTrialRestartNative]::IsWindowVisible($windowHandle)})
            }
            return $true
        }, [IntPtr]::Zero) | Out-Null
        if (@($trialWindows | Where-Object { $_.Visible -and -not $_.Host }).Count -gt 0) {
            throw 'Close the translation trial capture, pin and settings windows before restarting; no unsaved work was closed.'
        }
        $trialHost = $trialWindows | Where-Object Host | Select-Object -First 1
        if ($null -eq $trialHost) { throw 'Cannot locate the translation trial host; no force termination was attempted.' }
        $closeResult = [IntPtr]::Zero
        [void][QingSnapTrialRestartNative]::SendMessageTimeout($trialHost.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero, 2, 3000, [ref]$closeResult)
        [void][QingSnapTrialRestartNative]::PostThreadMessage($trialHost.ThreadId, 0x0012, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $trialInstance.WaitForExit(6000)) { throw 'The translation trial did not exit normally; no force termination was attempted.' }
    }
}

# Do not stop, replace, or send messages to the stable QingSnap process.
# A transient interactive task survives closing this chat; it is not autostart.
$taskName = 'QingSnap-TranslationTrial-' + [Guid]::NewGuid().ToString('N')
try {
    $principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $action = New-ScheduledTaskAction -Execute $resolvedTrialApp -WorkingDirectory (Split-Path -Parent $resolvedTrialApp) -Argument $LaunchArguments
    $options = New-ScheduledTaskSettingsSet -Priority 4 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $options | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 3
    $startedTrial = Get-Process QingSnap -ErrorAction SilentlyContinue | Where-Object Path -EQ $resolvedTrialApp
    if (-not $startedTrial) { throw 'Could not confirm the independently launched translation trial.' }
    $startedTrial.Refresh()
    if ($startedTrial.PriorityClass -ne [System.Diagnostics.ProcessPriorityClass]::Normal) {
        throw 'Translation trial is not running at Normal priority.'
    }
    $startedTrial | Select-Object Id, Path, Responding, PriorityClass, BasePriority
}
finally {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
}
