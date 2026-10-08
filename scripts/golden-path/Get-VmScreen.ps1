<#
.SYNOPSIS
  Saves what a Hyper-V VM's screen shows as a PNG, without a console session (#1158).

.DESCRIPTION
  Runs on the Hyper-V host. Asks Hyper-V for the VM's thumbnail at the size given (it comes back as 16-bit RGB565)
  and writes it to -OutPath, so a golden-path run can record what the person would have seen.
#>
param(
    [Parameter(Mandatory)] [string] $VmName,
    [Parameter(Mandatory)] [string] $OutPath,
    [int] $Width = 1280,
    [int] $Height = 800
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ns = 'root\virtualization\v2'
$vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$VmName'"
if (-not $vm) { throw "No VM named $VmName." }
$settings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData |
    Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' } | Select-Object -First 1
$service = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
$result = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{
    TargetSystem = $settings
    WidthPixels  = [uint16] $Width
    HeightPixels = [uint16] $Height
}
if ($result.ReturnValue -ne 0 -or -not $result.ImageData) { throw "Hyper-V gave no image (return $($result.ReturnValue))." }

$bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
$rect = New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)
$data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
try {
    $bytes = [byte[]] $result.ImageData
    for ($y = 0; $y -lt $Height; $y++) {
        [System.Runtime.InteropServices.Marshal]::Copy($bytes, $y * $Width * 2, [IntPtr]::Add($data.Scan0, $y * $data.Stride), $Width * 2)
    }
} finally {
    $bitmap.UnlockBits($data)
}
New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null
$bitmap.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
$OutPath
