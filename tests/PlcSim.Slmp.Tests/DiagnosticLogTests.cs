using PlcSim.Slmp.Core.Diagnostics;

namespace PlcSim.Slmp.Tests;

public sealed class DiagnosticLogTests
{
    [Fact]
    public void RotationBoundsFilesAndWriteFailuresDoNotEscape()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var log = new DiagnosticLog(directory.FullName, 512);
            for (var i = 0; i < 100; i++) log.Write(new string('a', 300));
            Assert.Equal(4, directory.GetFiles().Length);
            Assert.All(directory.GetFiles(), file => Assert.InRange(file.Length, 1, 512));
            Assert.True(File.Exists(Path.Combine(log.DirectoryPathForShell(), "diagnostic.log")));
            var invalidDirectory = Path.Combine(directory.FullName, "diagnostic.log");
            new DiagnosticLog(invalidDirectory).Write("Cannot create a directory over a file");
        }
        finally { directory.Delete(true); }
    }
}
