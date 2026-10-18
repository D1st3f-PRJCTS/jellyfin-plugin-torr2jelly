using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Torr2Jelly.Daemon;

public static class PlatformResolver
{
    public static string GetBinaryName()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var isOsx = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        if (isLinux)
        {
            return arch switch
            {
                Architecture.X64 => "TorrServer-linux-amd64",
                Architecture.Arm64 => "TorrServer-linux-arm64",
                Architecture.Arm => "TorrServer-linux-arm7",
                _ => "TorrServer-linux-amd64"
            };
        }

        if (isWindows)
        {
            return arch switch
            {
                Architecture.X64 => "TorrServer-windows-amd64.exe",
                Architecture.Arm64 => "TorrServer-windows-arm64.exe",
                _ => "TorrServer-windows-amd64.exe"
            };
        }

        if (isOsx)
        {
            return arch switch
            {
                Architecture.Arm64 => "TorrServer-darwin-arm64",
                Architecture.X64 => "TorrServer-darwin-amd64",
                _ => "TorrServer-darwin-arm64"
            };
        }

        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }

    public static string GetTargetExecutableName()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "torrserver.exe" : "torrserver";
    }

    public static string GetDownloadUrl(string binaryName)
    {
        return $"https://github.com/YouROK/TorrServer/releases/latest/download/{binaryName}";
    }
}
