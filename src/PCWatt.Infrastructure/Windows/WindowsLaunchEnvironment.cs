using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PCWatt.Infrastructure.Windows;

/// <summary>Detects restricted preview launches before touching the user's settings or database.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsLaunchEnvironment
{
    private const int TokenIntegrityLevel = 25;
    private const int MediumIntegrity = 0x2000;

    public static int GetIntegrityLevel()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        _ = GetTokenInformation(identity.AccessToken, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
        if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, TokenIntegrityLevel, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // TOKEN_MANDATORY_LABEL begins with SID_AND_ATTRIBUTES; its first field is the SID pointer.
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            return int.Parse(sid.AsSpan(sid.LastIndexOf('-') + 1), CultureInfo.InvariantCulture);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void Validate(int integrityLevel, string launchFile)
    {
        if (integrityLevel >= MediumIntegrity) return;
        throw new InvalidOperationException(
            "当前启动环境限制了本地数据写入，无法保存设置和记录。\n\n" +
            "请关闭此提示，在 Windows 资源管理器中双击以下启动文件：\n" +
            launchFile + "\n\n使用普通用户启动即可，无需管理员权限。");
    }

    public static string FindLaunchFile(string executableDirectory)
    {
        var directory = new DirectoryInfo(executableDirectory);
        var localLauncher = Path.Combine(directory.FullName, "Launch.cmd");
        if (File.Exists(localLauncher)) return localLauncher;
        while (directory is not null)
        {
            var launcher = Path.Combine(directory.FullName, "启动.cmd");
            if (File.Exists(Path.Combine(directory.FullName, "PCWatt.sln")) && File.Exists(launcher)) return launcher;
            directory = directory.Parent;
        }
        // A standalone extracted package also provides a normal Windows executable entry point.
        return Path.Combine(executableDirectory, "PCWatt.App.exe");
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
