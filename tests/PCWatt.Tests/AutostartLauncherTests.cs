using PCWatt.Infrastructure.Windows;

namespace PCWatt.Tests;

[TestClass]
public sealed class AutostartLauncherTests
{
    [TestMethod]
    public void UserLauncherDeploymentIsAtomicIdempotentAndKeepsApplicationOnOriginalDrive()
    {
        using var scope = new TemporaryRepository();
        var root = Path.GetDirectoryName(scope.DatabasePath)!;
        var application = Path.Combine(root, "软件 with space"); var source = Path.Combine(application, "launcher"); Directory.CreateDirectory(source);
        foreach (var name in new[] { "PCWatt.Launcher.exe", "PCWatt.Launcher.dll", "PCWatt.Launcher.runtimeconfig.json", "PCWatt.Launcher.deps.json" }) File.WriteAllText(Path.Combine(source, name), name);
        var installer = new AutostartLauncherInstaller(application, Path.Combine(root, "user"));
        var path = installer.Install(); var lastWrite = File.GetLastWriteTimeUtc(path);
        Assert.IsTrue(File.Exists(path)); installer.Install(); Assert.AreEqual(lastWrite, File.GetLastWriteTimeUtc(path));
        var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "target.json")));
        Assert.AreEqual(Path.Combine(application, "PCWatt.App.dll"), config.RootElement.GetProperty("ApplicationDll").GetString());
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [TestMethod]
    public void StartupRegistrationUsesDirectHiddenGuiLauncherAndRemovesCleanly()
    {
        var store = new Store(); var service = new AutostartService(store, @"C:\Users\User\AppData\Local\PCWatt\Launcher\PCWatt.Launcher.exe");
        service.SetEnabled(true); Assert.AreEqual("\"C:\\Users\\User\\AppData\\Local\\PCWatt\\Launcher\\PCWatt.Launcher.exe\" --background", store.Command);
        Assert.DoesNotContain("powershell", store.Command!); Assert.DoesNotContain("cmd.exe", store.Command!);
        service.SetEnabled(false); Assert.IsNull(store.Command);
    }
    [TestMethod]
    public void SelfContainedDeploymentStartsAppDirectlyWithoutSystemDotnetOrCopiedLauncher()
    {
        using var scope = new TemporaryRepository();
        var application = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "独立版本 with spaces");
        Directory.CreateDirectory(application);
        File.WriteAllText(Path.Combine(application, "PCWatt.App.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.WindowsDesktop.App\",\"version\":\"10.0.12\"}]}}");
        File.WriteAllText(Path.Combine(application, "PCWatt.App.exe"), "test fixture");
        var data = Path.Combine(application, "user-data");
        var path = new AutostartLauncherInstaller(application, data).Install();
        Assert.AreEqual(Path.Combine(application, "PCWatt.App.exe"), path);
        Assert.AreEqual(path, AutostartLauncherInstaller.LaunchPath(application, data));
        Assert.IsFalse(Directory.Exists(data));
        var store = new Store(); new AutostartService(store, path).SetEnabled(true);
        Assert.AreEqual($"\"{path}\" --background", store.Command);
        var elevation = AdministratorLaunch.CreateStartInfo(path, 123, true);
        Assert.AreEqual(path, elevation.FileName); Assert.AreEqual("runas", elevation.Verb);
        CollectionAssert.AreEqual(new[] { "--elevation-handoff", "123", "--background" }, elevation.ArgumentList.ToArray());
    }

    [TestMethod]
    public void FrameworkDependentBuildKeepsExistingLauncherPath()
    {
        using var scope = new TemporaryRepository(); var directory = Path.GetDirectoryName(scope.DatabasePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "PCWatt.App.runtimeconfig.json"), "{\"runtimeOptions\":{\"frameworks\":[{\"name\":\"Microsoft.WindowsDesktop.App\",\"version\":\"10.0.12\"}]}}");
        Assert.IsFalse(AutostartLauncherInstaller.IsSelfContained(directory));
        Assert.AreEqual(Path.Combine(directory, "user", "Launcher", "PCWatt.Launcher.exe"), AutostartLauncherInstaller.LaunchPath(directory, Path.Combine(directory, "user")));
    }

    [TestMethod]
    public void MissingStandaloneExecutableFailsBeforeWritingAnyUserFiles()
    {
        using var scope = new TemporaryRepository(); var directory = Path.GetDirectoryName(scope.DatabasePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "PCWatt.App.runtimeconfig.json"), "{\"runtimeOptions\":{\"includedFrameworks\":[]}}");
        var user = Path.Combine(directory, "user");
        Assert.Throws<FileNotFoundException>(() => new AutostartLauncherInstaller(directory, user).Install());
        Assert.IsFalse(Directory.Exists(user));
    }

    private sealed class Store : IAutostartStore
    {
        public string? Command;
        public string? Read() => Command;
        public void Write(string command) => Command = command;
        public void Remove() => Command = null;
    }
}
