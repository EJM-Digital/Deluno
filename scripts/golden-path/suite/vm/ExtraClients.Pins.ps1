<#
    What the suite installs in the golden VM for uTorrent and rTorrent (#1276). Change a version here, never in the script. A file that
    fails its SHA-256 is deleted and the run says so. Dot-sourced by ExtraClients.Lib.ps1 (only .ps1 files are sent to the VM).
#>
$script:XcPins = @{
    # uTorrent Classic for Windows, the version Deluno's recordings (tests/fixtures/recorded/utorrent) were made on.
    # BitTorrent Inc. publishes no checksum for its installer, so none can be pinned from the vendor. Until Sha256 is filled in, the
    # installer must carry the vendor's valid Authenticode signature and the installed uTorrent.exe must be exactly this version;
    # the installer's own SHA-256 is written to the run log and the notes, to be pinned from the first run.
    UTorrent = @{
        Version = '3.6.0.47254'
        Url     = 'https://download-new.utorrent.com/endpoint/utorrent/os/windows/track/stable/'
        Signer  = 'BitTorrent|Rainberry|uTorrent'
        Sha256  = ''
    }
    # rTorrent has no Windows build. It runs from Ubuntu's own package inside WSL1, which needs no virtualization (the VM has none for
    # WSL2 or Docker). The root file system is Canonical's dated 22.04 image, pinned by the SHA-256 Canonical publishes for it.
    Wsl = @{
        Distro       = 'deluno-rt'
        RootfsName   = 'ubuntu-jammy-wsl-amd64-ubuntu22.04lts.rootfs.tar.gz'
        RootfsUrl    = 'https://cloud-images.ubuntu.com/wsl/jammy/20250318/ubuntu-jammy-wsl-amd64-ubuntu22.04lts.rootfs.tar.gz'
        RootfsSha256 = '1483cc5c1dce13064f774834cbffdff226559fd522a67a381a8ea77d63fb4109'
    }
    # 0.9.8-1build1 is what Ubuntu 22.04 publishes (apt checks its signed package lists). The recordings in tests/fixtures/recorded/rtorrent
    # are of 0.16.25 (a container image), which needs Docker and so cannot run in a Hyper-V guest without virtualization; every
    # XML-RPC call Deluno sends exists in both.
    Rtorrent = @{
        Version  = '0.9.8'
        Package  = 'rtorrent=0.9.8-1build1'
        Packages = 'nginx tmux'
    }
    Ports = @{ utorrent = 8087; rtorrent = 8086 }
    # Both keep a film's data in the folder Deluno's libraries look in for finished downloads, like the picker-installed clients do.
    DataFolder = 'C:\Downloads\Completed\Movies'
}
