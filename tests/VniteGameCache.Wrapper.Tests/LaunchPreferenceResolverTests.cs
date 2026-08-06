using VniteGameCache.Contracts;
using VniteGameCache.Wrapper;

namespace VniteGameCache.Wrapper.Tests;

public sealed class LaunchPreferenceResolverTests
{
    [Fact]
    public void Direct_remains_the_default_launch_mode()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");

        var plan = fixture.Resolver.Resolve("game-direct", gameExecutable, fixture.GameRoot, new VgcSettings());

        Assert.Equal(VgcLaunchMode.Direct, plan.Mode);
        Assert.Equal(gameExecutable, plan.PrimaryProcess.FileName);
        Assert.True(plan.UseOriginalScript);
        Assert.Empty(plan.SecondaryProcesses);
    }

    [Fact]
    public void Translation_mode_prefers_paired_zh_cn_executable()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        var translatedExecutable = fixture.CreateGameFile("Game_zh-CN.exe");

        var plan = fixture.Resolver.Resolve(
            "game-cn",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings { DefaultLaunchMode = "TranslationPreferred" });

        Assert.Equal(VgcLaunchMode.TranslationPreferred, plan.Mode);
        Assert.Equal(translatedExecutable, plan.TrackedExecutablePath);
        Assert.False(plan.UseOriginalScript);
    }

    [Fact]
    public void Translation_mode_prefers_paired_ai_model_executable()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("jiboku.exe");
        var translatedExecutable = fixture.CreateGameFile("jiboku-claude-3-opus.exe");

        var selected = fixture.Resolver.SelectPreferredTranslationExecutable(gameExecutable);

        Assert.Equal(translatedExecutable, selected);
    }

    [Fact]
    public void Translation_mode_does_not_treat_letters_inside_main_as_ai_marker()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        fixture.CreateGameFile("Main.exe");

        var selected = fixture.Resolver.SelectPreferredTranslationExecutable(gameExecutable);

        Assert.Equal(gameExecutable, selected);
    }

    [Fact]
    public void Translation_mode_falls_back_to_direct_when_no_paired_translation_exists()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        fixture.CreateGameFile("Other_CN.exe");

        var plan = fixture.Resolver.Resolve(
            "game-fallback",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings { DefaultLaunchMode = "TranslationPreferred" });

        Assert.Equal(VgcLaunchMode.Direct, plan.Mode);
        Assert.Equal(gameExecutable, plan.TrackedExecutablePath);
    }

    [Fact]
    public void Mtool_mode_rebuilds_verified_template_with_current_components()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        fixture.CreateMToolTemplate("Game.exe", "PythonHook64.dll");
        var mtoolExecutable = fixture.CreateMToolInstallation("PythonHook64.dll");

        var plan = fixture.Resolver.Resolve(
            "game-mtool",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings
            {
                DefaultLaunchMode = "MTool",
                MToolExecutablePath = mtoolExecutable
            });

        Assert.Equal(VgcLaunchMode.MTool, plan.Mode);
        Assert.Equal(Path.Combine(fixture.MToolRoot, "loaders", "inject.exe"), plan.PrimaryProcess.FileName);
        Assert.Equal(Path.Combine(fixture.JunctionRoot, "Game.exe"), plan.PrimaryProcess.Arguments[0]);
        Assert.Equal(Path.Combine(fixture.MToolRoot, "loaders", "PythonHook64.dll"), plan.PrimaryProcess.Arguments[1]);
        Assert.Equal(Path.Combine(fixture.MToolRoot, "nw.exe"), Assert.Single(plan.SecondaryProcesses).FileName);
        Assert.Equal(gameExecutable, plan.TrackedExecutablePath);
        Assert.Equal(fixture.JunctionRoot, Assert.Single(plan.AdditionalTrackingRoots));
        Assert.DoesNotContain("D:\\OldGame", string.Join('|', plan.PrimaryProcess.Arguments), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E:\\OldMTool", string.Join('|', plan.PrimaryProcess.Arguments), StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Mtool_mode_accepts_real_batch_template_with_public_junction()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("AnotherHoleToSeduce.exe");
        fixture.CreateRealMToolTemplate("AnotherHoleToSeduce.exe", "PythonHook64.dll");
        var mtoolExecutable = fixture.CreateMToolInstallation("PythonHook64.dll");

        var plan = fixture.Resolver.Resolve(
            "game-real-mtool",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings
            {
                DefaultLaunchMode = "MTool",
                MToolExecutablePath = mtoolExecutable
            });

        Assert.Equal(VgcLaunchMode.MTool, plan.Mode);
        Assert.Equal(Path.Combine(fixture.JunctionRoot, "AnotherHoleToSeduce.exe"), plan.PrimaryProcess.Arguments[0]);
        Assert.DoesNotContain("C:\\Users\\Public\\MTool_Game_Tmp", string.Join('|', plan.PrimaryProcess.Arguments), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Mtool_mode_falls_back_to_translation_when_no_verified_batch_exists()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        var translatedExecutable = fixture.CreateGameFile("Game_CHS.exe");

        var plan = fixture.Resolver.Resolve(
            "game-no-mtool",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings { DefaultLaunchMode = "MTool" });

        Assert.Equal(VgcLaunchMode.TranslationPreferred, plan.Mode);
        Assert.Equal(translatedExecutable, plan.TrackedExecutablePath);
    }

    [Fact]
    public void Mtool_mode_ignores_arbitrary_batch_files()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        File.WriteAllText(Path.Combine(fixture.GameRoot, "launch.bat"), "start calc.exe");

        var plan = fixture.Resolver.Resolve(
            "game-arbitrary-bat",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings { DefaultLaunchMode = "MTool" });

        Assert.Equal(VgcLaunchMode.Direct, plan.Mode);
        Assert.Equal(gameExecutable, plan.TrackedExecutablePath);
    }

    [Fact]
    public void Mtool_mode_fails_closed_when_recognized_batch_is_invalid()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        File.WriteAllText(
            Path.Combine(fixture.GameRoot, "与工具一同启动.bat"),
            "REM This file is automatically generated by the MTool.\r\necho invalid");

        var error = Assert.Throws<LaunchPreferenceException>(() => fixture.Resolver.Resolve(
            "game-invalid-bat",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings { DefaultLaunchMode = "MTool" }));

        Assert.Equal("mtool_batch_invalid", error.Code);
    }

    [Fact]
    public void Mtool_mode_fails_closed_when_current_component_is_missing()
    {
        using var fixture = new LaunchFixture();
        var gameExecutable = fixture.CreateGameFile("Game.exe");
        fixture.CreateMToolTemplate("Game.exe", "PythonHook64.dll");
        var mtoolExecutable = fixture.CreateMToolInstallation(loaderName: null);

        var error = Assert.Throws<LaunchPreferenceException>(() => fixture.Resolver.Resolve(
            "game-missing-loader",
            gameExecutable,
            fixture.GameRoot,
            new VgcSettings
            {
                DefaultLaunchMode = "MTool",
                MToolExecutablePath = mtoolExecutable
            }));

        Assert.Equal("mtool_component_missing", error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Direct")]
    [InlineData("CustomLauncher")]
    [InlineData("unknown")]
    public void Legacy_or_unknown_launch_modes_normalize_to_direct(string? value)
    {
        Assert.Equal(VgcLaunchMode.Direct, VgcSettingsStore.ParseLaunchMode(value));
    }

    [Fact]
    public void Windows_junction_manager_creates_and_retargets_only_its_owned_link()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "vgc-mtool-junction-tests", Guid.NewGuid().ToString("N"));
        var links = Path.Combine(root, "links");
        var firstTarget = Path.Combine(root, "first-target");
        var secondTarget = Path.Combine(root, "second-target");
        Directory.CreateDirectory(firstTarget);
        Directory.CreateDirectory(secondTarget);
        File.WriteAllText(Path.Combine(firstTarget, "first.txt"), "first");
        File.WriteAllText(Path.Combine(secondTarget, "second.txt"), "second");
        string? junction = null;
        try
        {
            var manager = new WindowsMToolJunctionManager(links);

            junction = manager.EnsureJunction("junction-game", firstTarget);
            Assert.True(File.Exists(Path.Combine(junction, "first.txt")));

            junction = manager.EnsureJunction("junction-game", secondTarget);
            Assert.False(File.Exists(Path.Combine(junction, "first.txt")));
            Assert.True(File.Exists(Path.Combine(junction, "second.txt")));
            Assert.True(File.Exists(Path.Combine(firstTarget, "first.txt")));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(junction) && Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class LaunchFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vgc-launch-resolver", Guid.NewGuid().ToString("N"));

        public LaunchFixture()
        {
            GameRoot = Path.Combine(_root, "game");
            MToolRoot = Path.Combine(_root, "mtool");
            JunctionRoot = Path.Combine(_root, "junction");
            Directory.CreateDirectory(GameRoot);
            Resolver = new LaunchPreferenceResolver(new FakeJunctionManager(JunctionRoot));
        }

        public string GameRoot { get; }
        public string MToolRoot { get; }
        public string JunctionRoot { get; }
        public LaunchPreferenceResolver Resolver { get; }

        public string CreateGameFile(string name)
        {
            var path = Path.Combine(GameRoot, name);
            File.WriteAllText(path, name);
            return Path.GetFullPath(path);
        }

        public void CreateMToolTemplate(string gameExecutableName, string loaderName)
        {
            File.WriteAllText(
                Path.Combine(GameRoot, "与工具一同启动.bat"),
                $"REM This file is automatically generated by the MTool.\r\n\"E:\\OldMTool\\loaders\\inject.exe\" \"D:\\OldGame\\{gameExecutableName}\" \"E:\\OldMTool\\loaders\\{loaderName}\"\r\nstart \"\" \"E:\\OldMTool\\nw.exe\" \"E:\\OldMTool\"");
        }

        public void CreateRealMToolTemplate(string gameExecutableName, string loaderName)
        {
            File.WriteAllText(
                Path.Combine(GameRoot, "与工具一同启动.bat"),
                $"chcp 65001\r\n@cd /d \"C:\\Users\\Public\\MTool_Game_Tmp\\4b7f55fe1bf38660daec253a31a9f009\"\r\n\"E:\\MTool\\Tool\\loaders\\inject.exe\" \"C:\\Users\\Public\\MTool_Game_Tmp\\4b7f55fe1bf38660daec253a31a9f009\\{gameExecutableName}\" \"E:\\MTool\\Tool\\loaders\\{loaderName}\"\r\nstart \"\" \"E:\\MTool\\Tool\\nw.exe\" \"E:\\MTool\\Tool\"\r\n@echo \"Please do not modify this file, it is automatically generated by the MTool.\"");
        }
        public string CreateMToolInstallation(string? loaderName)
        {
            var loaders = Path.Combine(MToolRoot, "loaders");
            Directory.CreateDirectory(loaders);
            File.WriteAllText(Path.Combine(loaders, "inject.exe"), "inject");
            if (!string.IsNullOrWhiteSpace(loaderName))
            {
                File.WriteAllText(Path.Combine(loaders, loaderName), "loader");
            }

            File.WriteAllText(Path.Combine(MToolRoot, "nw.exe"), "nw");
            var executable = Path.Combine(MToolRoot, "MTool.exe");
            File.WriteAllText(executable, "mtool");
            return executable;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeJunctionManager(string junctionRoot) : IMToolJunctionManager
    {
        public string EnsureJunction(string gameId, string targetDirectory) => Path.GetFullPath(junctionRoot);
    }
}


