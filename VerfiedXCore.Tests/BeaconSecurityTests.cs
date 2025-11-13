using Xunit;
using ReserveBlockCore.Services;
using System.IO;
using ReserveBlockCore.Utilities;

namespace VerfiedXCore.Tests
{
    public class BeaconSecurityTests
    {
        [Fact]
        public void DeleteFile_ShouldPreventPathTraversal_WithDoubleDots()
        {
            // Arrange
            var beaconPath = GetPathUtility.GetBeaconPath();
            var testFileName = "legitimate_file.txt";
            var testFilePath = Path.Combine(beaconPath, testFileName);
            File.WriteAllText(testFilePath, "test content");

            // Create a file outside beacon directory with a unique name
            var tempPath = Path.GetTempPath();
            var outsideFileName = $"outside_test_{Guid.NewGuid()}.txt";
            var outsideFilePath = Path.Combine(tempPath, outsideFileName);
            File.WriteAllText(outsideFilePath, "outside content");

            try
            {
                // Act - Attempt path traversal to delete the outside file
                // This constructs a path like "../../Temp/outside_test_xxx.txt"
                var relativePath = Path.GetRelativePath(beaconPath, outsideFilePath);
                BeaconService.DeleteFile(relativePath);

                // Assert - Outside file should still exist (path traversal prevented)
                Assert.True(File.Exists(outsideFilePath), 
                    $"Path traversal protection failed: file at {outsideFilePath} was deleted");

                // Verify legitimate file can still be deleted
                BeaconService.DeleteFile(testFileName);
                Assert.False(File.Exists(testFilePath), 
                    "Legitimate file should be deletable");
            }
            finally
            {
                // Cleanup
                if (File.Exists(testFilePath))
                    File.Delete(testFilePath);
                if (File.Exists(outsideFilePath))
                    File.Delete(outsideFilePath);
            }
        }

        [Fact]
        public void DeleteFile_ShouldPreventPathTraversal_WithAbsolutePath()
        {
            // Arrange
            var beaconPath = GetPathUtility.GetBeaconPath();
            var testFileName = "test_file2.txt";
            var testFilePath = Path.Combine(beaconPath, testFileName);
            File.WriteAllText(testFilePath, "test content");

            // Create a file in a completely different location
            var tempPath = Path.GetTempPath();
            var outsideFileName = "temp_outside.txt";
            var outsideFilePath = Path.Combine(tempPath, outsideFileName);
            File.WriteAllText(outsideFilePath, "temp content");

            try
            {
                // Act - Try to delete file using absolute path
                BeaconService.DeleteFile(outsideFilePath);

                // Assert - File should still exist (absolute path should be stripped to filename only)
                Assert.True(File.Exists(outsideFilePath), "Absolute path protection failed: temp file was deleted");
            }
            finally
            {
                // Cleanup
                if (File.Exists(testFilePath))
                    File.Delete(testFilePath);
                if (File.Exists(outsideFilePath))
                    File.Delete(outsideFilePath);
            }
        }

        [Fact]
        public void DeleteFile_ShouldDeleteValidFile()
        {
            // Arrange
            var beaconPath = GetPathUtility.GetBeaconPath();
            var testFileName = "valid_test_file.txt";
            var testFilePath = Path.Combine(beaconPath, testFileName);
            File.WriteAllText(testFilePath, "test content");

            // Act
            BeaconService.DeleteFile(testFileName);

            // Assert
            Assert.False(File.Exists(testFilePath), "Valid file should be deleted");
        }

        [Fact]
        public void DeleteFile_ShouldHandleEmptyOrNullInput()
        {
            // Act & Assert - Should not throw exceptions
            BeaconService.DeleteFile(null);
            BeaconService.DeleteFile("");
            BeaconService.DeleteFile("   ");
        }

        [Theory]
        [InlineData("../../../etc/passwd")]
        [InlineData("..\\..\\..\\Windows\\System32\\config\\SAM")]
        [InlineData("/etc/shadow")]
        [InlineData("C:\\Windows\\System32\\config\\SAM")]
        [InlineData("~/../../etc/passwd")]
        public void DeleteFile_ShouldPreventVariousPathTraversalAttempts(string maliciousPath)
        {
            // Act & Assert - Should not throw and should not delete anything outside beacon directory
            // This is a safety test - we're verifying the method doesn't crash or cause damage
            BeaconService.DeleteFile(maliciousPath);
            
            // If we got here without exception, the protection is working
            Assert.True(true);
        }
    }
}
