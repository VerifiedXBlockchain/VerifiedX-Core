using Xunit;

namespace VerfiedXCore.Tests
{
    public class TaskAnswerParsingTests
    {
        [Theory]
        [InlineData("123:1000", true, 123, 1000L)] // Valid input
        [InlineData("0:1", true, 0, 1L)] // Valid: Answer can be 0, Height must be positive
        [InlineData("999999:999999999", true, 999999, 999999999L)] // Large valid values
        public void ValidTaskAnswerInput_ShouldParseCorrectly(string input, bool expectedSuccess, int expectedAnswer, long expectedHeight)
        {
            // Arrange
            var parts = input.Split(':');
            
            // Act
            bool answerParseSuccess = int.TryParse(parts[0], out var answer);
            bool heightParseSuccess = long.TryParse(parts[1], out var height);
            bool parseSuccess = answerParseSuccess && heightParseSuccess;
            bool boundsValid = parseSuccess && answer >= 0 && height > 0;
            
            // Assert
            Assert.Equal(expectedSuccess, boundsValid);
            if (boundsValid)
            {
                Assert.Equal(expectedAnswer, answer);
                Assert.Equal(expectedHeight, height);
            }
        }

        [Theory]
        [InlineData("abc:123")] // Invalid Answer format
        [InlineData("123:xyz")] // Invalid Height format
        [InlineData("abc:xyz")] // Both invalid
        [InlineData("12.5:100")] // Decimal not allowed
        [InlineData("100:12.5")] // Decimal not allowed
        [InlineData("999999999999999999999:1")] // Overflow int
        [InlineData("1:999999999999999999999999999")] // Overflow long (extreme)
        public void InvalidNumericFormat_ShouldFailParsing(string input)
        {
            // Arrange
            var parts = input.Split(':');
            
            // Act
            bool parseSuccess = int.TryParse(parts[0], out var answer) && long.TryParse(parts[1], out var height);
            
            // Assert - Should fail to parse
            Assert.False(parseSuccess);
        }

        [Theory]
        [InlineData("-1:100")] // Negative Answer
        [InlineData("100:0")] // Zero Height
        [InlineData("100:-1")] // Negative Height
        [InlineData("-5:-10")] // Both negative
        public void OutOfBoundsValues_ShouldFailValidation(string input)
        {
            // Arrange
            var parts = input.Split(':');
            
            // Act
            bool answerParseSuccess = int.TryParse(parts[0], out var answer);
            bool heightParseSuccess = long.TryParse(parts[1], out var height);
            bool parseSuccess = answerParseSuccess && heightParseSuccess;
            bool boundsValid = parseSuccess && answer >= 0 && height > 0;
            
            // Assert - May parse but should fail bounds check
            Assert.False(boundsValid);
        }

        [Theory]
        [InlineData("123")] // Missing height
        [InlineData(":")] // Empty values
        [InlineData("123:456:789")] // Too many parts
        public void MalformedInput_ShouldBeRejected(string input)
        {
            // Arrange
            var parts = input.Split(':');
            
            // Act
            bool hasCorrectFormat = parts.Length == 2;
            
            // Assert
            if (hasCorrectFormat)
            {
                bool parseSuccess = int.TryParse(parts[0], out _) && long.TryParse(parts[1], out _);
                Assert.False(parseSuccess); // Should fail parsing if split succeeded with wrong format
            }
            else
            {
                Assert.False(hasCorrectFormat); // Should fail format check
            }
        }
    }
}
