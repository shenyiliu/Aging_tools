param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
if (!$ReleaseDirectory) {
    $ReleaseDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\GW_AgingTool_win-x64'
}

# Map PE resources as data only. Never execute an application on the build host.
if (!('Aging.Build.ManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace Aging.Build {
    public static class ManifestReader {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr resource);
        public static string Read(string path) {
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x02 | 0x20);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
            try {
                IntPtr resource = FindResourceW(module, new IntPtr(1), new IntPtr(24));
                if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
                uint size = SizeofResource(module, resource);
                IntPtr loaded = LoadResource(module, resource);
                IntPtr address = LockResource(loaded);
                if (size == 0 || loaded == IntPtr.Zero || address == IntPtr.Zero)
                    throw new InvalidOperationException("Cannot read manifest: " + path);
                byte[] bytes = new byte[checked((int)size)];
                Marshal.Copy(address, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\0');
            } finally { FreeLibrary(module); }
        }
    }
}
'@
}

$entries = @('Test.exe', 'Other\ControlPanel.exe', 'Other\ViewLOG.exe',
    'Reboot\Reboot.exe', 'Sleep\Sleep.exe', 'BurnInTest\BurnInTest.exe',
    'FurMark\FurMarkTest.exe', '3DMark\3DMarkTest.exe')
foreach ($entry in $entries) {
    $path = [IO.Path]::GetFullPath((Join-Path $ReleaseDirectory $entry))
    [xml]$manifest = [Aging.Build.ManifestReader]::Read($path)
    $level = $manifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='trustInfo']/*[local-name()='security']/*[local-name()='requestedPrivileges']/*[local-name()='requestedExecutionLevel']")
    if (!$level -or $level.GetAttribute('level') -ne 'requireAdministrator' -or $level.GetAttribute('uiAccess') -ne 'false') {
        throw ('Executable must request administrator privileges with uiAccess=false: ' + $entry)
    }
    [pscustomobject]@{ Entry = $entry; ExecutionLevel = $level.GetAttribute('level'); Executed = $false }
}
