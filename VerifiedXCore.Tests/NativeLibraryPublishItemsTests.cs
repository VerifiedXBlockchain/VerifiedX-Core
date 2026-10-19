using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>Every native library the node ships is copied to the publish folder explicitly, not by an SDK default.</summary>
    public class NativeLibraryPublishItemsTests
    {
        [Fact]
        public void EveryPlonkAndFrostLibrary_IsCopiedOnPublish()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VerifiedXCore", "VerifiedXCore.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var project = XDocument.Load(Path.Combine(dir!.FullName, "VerifiedXCore", "VerifiedXCore.csproj"));

            var natives = project.Descendants("None")
                .Where(n => ((string?)n.Attribute("Include") ?? (string?)n.Attribute("Update") ?? "") is var path
                            && (path.StartsWith("Plonk\\") || path.StartsWith("Frost\\")))
                .ToList();
            Assert.NotEmpty(natives);
            Assert.All(natives, n => Assert.Equal("PreserveNewest", (string?)n.Element("CopyToPublishDirectory")));
        }
    }
}
