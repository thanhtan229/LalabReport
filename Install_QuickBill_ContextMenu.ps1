# Script dang ky menu chuot phai QUICK BILL cho Lalab Auto Report
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExePath = Join-Path $ScriptDir "src\LalabAutoReport.UI\bin\Debug\net10.0-windows\LalabAutoReport.UI.exe"

if (-not (Test-Path $ExePath)) {
    $ExePath = Join-Path $ScriptDir "src\LalabAutoReport.UI\bin\Release\net10.0-windows\win-x64\publish\LalabAutoReport.UI.exe"
}

if (-not (Test-Path $ExePath)) {
    $ExePath = Join-Path $ScriptDir "src\LalabAutoReport.UI\bin\Release\net10.0-windows\LalabAutoReport.UI.exe"
}

if (-not (Test-Path $ExePath)) {
    $ExePath = Join-Path $ScriptDir "LalabAutoReport.UI.exe"
}

if (-not (Test-Path $ExePath)) {
    Write-Host "[ERROR] Khong tim thay LalabAutoReport.UI.exe tai $ExePath" -ForegroundColor Red
    exit 1
}

Write-Host "Dang dang ky menu chuot phai cho: $ExePath" -ForegroundColor Cyan

$MenuTitle = [char]0x26A1 + " QUICK BILL (Lalab)"

$IconPath = Join-Path $ScriptDir "quick_bill.ico"
if (-not (Test-Path $IconPath)) {
    $IconPath = Join-Path (Split-Path -Parent $ExePath) "Resources\quick_bill.ico"
}
if (-not (Test-Path $IconPath)) {
    $IconPath = Join-Path $ScriptDir "src\LalabAutoReport.UI\Resources\quick_bill.ico"
}
$IconValue = if (Test-Path $IconPath) { "`"$IconPath`"" } else { "`"$ExePath`",0" }

# 1. Directory (Chuot phai tren thu muc)
$dirKey = "HKCU:\Software\Classes\Directory\shell\LalabQuickBill"
New-Item -Path $dirKey -Force | Out-Null
Set-ItemProperty -Path $dirKey -Name "(Default)" -Value $MenuTitle
Set-ItemProperty -Path $dirKey -Name "Icon" -Value $IconValue

$dirCmdKey = "$dirKey\command"
New-Item -Path $dirCmdKey -Force | Out-Null
Set-ItemProperty -Path $dirCmdKey -Name "(Default)" -Value "`"$ExePath`" --quick-bill `"%1`""

# 2. Directory Background (Chuot phai trong vung trong thu muc)
$bgKey = "HKCU:\Software\Classes\Directory\Background\shell\LalabQuickBill"
New-Item -Path $bgKey -Force | Out-Null
Set-ItemProperty -Path $bgKey -Name "(Default)" -Value $MenuTitle
Set-ItemProperty -Path $bgKey -Name "Icon" -Value $IconValue

$bgCmdKey = "$bgKey\command"
New-Item -Path $bgCmdKey -Force | Out-Null
Set-ItemProperty -Path $bgCmdKey -Name "(Default)" -Value "`"$ExePath`" --quick-bill `"%V`""

# 3. TOGGLE PRINTED (ĐÃ IN) - Directory (Chuot phai tren thu muc)
$printTitle = [char]::ConvertFromUtf32(0x1F3F7) + [char]0xFE0F + " ĐÃ IN (Lalab)"

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$printIconPath = Join-Path $localAppData "LalabAutoReport\Assets\folder_printed.ico"
$printIconValue = if (Test-Path $printIconPath) { "`"$printIconPath`"" } else { $IconValue }

$printDirKey = "HKCU:\Software\Classes\Directory\shell\LalabTogglePrinted"
New-Item -Path $printDirKey -Force | Out-Null
Set-ItemProperty -Path $printDirKey -Name "(Default)" -Value $printTitle
Set-ItemProperty -Path $printDirKey -Name "Icon" -Value $printIconValue

$printDirCmdKey = "$printDirKey\command"
New-Item -Path $printDirCmdKey -Force | Out-Null
Set-ItemProperty -Path $printDirCmdKey -Name "(Default)" -Value "`"$ExePath`" --toggle-printed `"%1`""

# 4. TOGGLE PRINTED (ĐÃ IN) - Directory Background (Chuot phai trong vung trong thu muc)
$printBgKey = "HKCU:\Software\Classes\Directory\Background\shell\LalabTogglePrinted"
New-Item -Path $printBgKey -Force | Out-Null
Set-ItemProperty -Path $printBgKey -Name "(Default)" -Value $printTitle
Set-ItemProperty -Path $printBgKey -Name "Icon" -Value $printIconValue

$printBgCmdKey = "$printBgKey\command"
New-Item -Path $printBgCmdKey -Force | Out-Null
# 5. Flush Windows Explorer Context Menu Icon Cache
$code = @"
using System;
using System.Runtime.InteropServices;
public class ContextMenuNotifier {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    public static void Flush() {
        SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED, SHCNF_FLUSH
    }
}
"@
try {
    Add-Type -TypeDefinition $code -Language CSharp -ErrorAction SilentlyContinue
    [ContextMenuNotifier]::Flush()
} catch { }

Write-Host "[SUCCESS] Da dang ky menu chuot phai QUICK BILL va ĐÃ IN thanh cong!" -ForegroundColor Green
