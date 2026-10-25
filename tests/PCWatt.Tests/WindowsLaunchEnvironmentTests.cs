using PCWatt.Infrastructure.Windows;

namespace PCWatt.Tests;

[TestClass]
public sealed class WindowsLaunchEnvironmentTests
{
    [TestMethod]
    public void CurrentWindowsTokenCanBeReadWithoutAdministratorRights()
    {
        var level = WindowsLaunchEnvironment.GetIntegrityLevel();
        Assert.IsGreaterThanOrEqualTo(0, level);
        Assert.IsLessThanOrEqualTo(0x5000, level);
        WindowsLaunchEnvironment.Validate(level, @"D:\工作间\PCWatt");
    }

    [TestMethod]
    [DataRow(0)] [DataRow(0x1000)]
    public void RestrictedLaunchStopsBeforeWritingAndExplainsTheNormalUserEntryPoint(int integrity)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsLaunchEnvironment.Validate(integrity, @"D:\工作间\PCWatt\启动.cmd"));
        Assert.Contains("Windows 资源管理器", error.Message);
        Assert.Contains("启动.cmd", error.Message);
        Assert.Contains(@"D:\工作间\PCWatt", error.Message);
        Assert.Contains("无需管理员权限", error.Message);
    }

    [TestMethod]
    [DataRow(0x2000)] [DataRow(0x3000)]
    public void OrdinaryWindowsUserIsAllowedWithoutChangingSystemPermissions(int integrity) =>
        WindowsLaunchEnvironment.Validate(integrity, @"D:\工作间\PCWatt");
}
