using VniteGameCache.ControlPanel;

namespace VniteGameCache.ControlPanel.Tests;

public sealed class DirectorySizeProbeTests
{
    [Fact]
    public void Measures_only_selected_directory_and_nested_files()
    {
        var root = Directory.CreateTempSubdirectory("vgc-size-probe-").FullName;
        try
        {
            var selected = Directory.CreateDirectory(Path.Combine(root, "selected")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(selected, "save")).FullName;
            File.WriteAllBytes(Path.Combine(selected, "game.exe"), new byte[17]);
            File.WriteAllBytes(Path.Combine(nested, "slot.sav"), new byte[5]);
            File.WriteAllBytes(Path.Combine(root, "other.zip"), new byte[100]);

            var result = DirectorySizeProbe.Measure(selected, CancellationToken.None);

            Assert.Equal(22, result.Bytes);
            Assert.Equal(2, result.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Honors_cancellation_before_scanning()
    {
        var root = Directory.CreateTempSubdirectory("vgc-size-probe-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => DirectorySizeProbe.Measure(root, cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
