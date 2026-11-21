using System;
using System.IO;
using ImageMagick;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A received NFT's files are read for thumbnails only with the decoder their extension names, under an ImageMagick
    /// policy that allows only the thumbnail formats, and validators and casters make no thumbnails at all.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NftThumbnailImagingTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"thumbs_{Guid.NewGuid():N}");
        private readonly string? _priorValidator = Globals.ValidatorAddress;

        public NftThumbnailImagingTests()
        {
            Directory.CreateDirectory(_dir);
            Globals.ValidatorAddress = "";
            // As the first thumbnail does (called by name so this file also builds against code without it).
            typeof(NFTAssetFileUtility).GetMethod("EnsureImagingPolicy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
        }

        public void Dispose()
        {
            Globals.ValidatorAddress = _priorValidator;
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Image(string name, MagickFormat format)
        {
            var path = Path.Combine(_dir, name);
            using var image = new MagickImage(MagickColors.Red, 600, 600);
            image.Write(path, format);
            return path;
        }

        private string ThumbOf(string name) => Path.Combine(_dir, "thumb-" + Path.GetFileNameWithoutExtension(name) + ".jpg");

        [Fact]
        public void APng_GetsItsThumbnail()
        {
            var png = Image("art.png", MagickFormat.Png);
            NFTAssetFileUtility.CreateNFTAssetThumbnail(png, Path.Combine(_dir, "thumb-art.png"));
            Assert.True(File.Exists(ThumbOf("art.png")));
        }

        [Theory]
        [InlineData("art.png", MagickFormat.Png)]
        [InlineData("art.jpg", MagickFormat.Jpeg)]
        [InlineData("art.jpeg", MagickFormat.Jpeg)]
        [InlineData("art.jp2", MagickFormat.Jp2)]
        [InlineData("art.gif", MagickFormat.Gif)]
        [InlineData("art.tif", MagickFormat.Tiff)]
        [InlineData("art.tiff", MagickFormat.Tiff)]
        [InlineData("art.webp", MagickFormat.WebP)]
        [InlineData("art.bmp", MagickFormat.Bmp)]
        public void EveryThumbnailFormat_GetsItsThumbnail(string name, MagickFormat format)
        {
            var file = Image(name, format);
            NFTAssetFileUtility.CreateNFTAssetThumbnail(file, Path.Combine(_dir, "thumb-" + name));
            Assert.True(File.Exists(ThumbOf(name)), $"no thumbnail for {name} (ImageMagick {MagickNET.ImageMagickVersion})");
        }

        [Fact]
        public void APdf_StillGetsItsThumbnail_UnderThePolicy()
        {
            // A one-page PDF (PDFium renders it; ImageMagick only receives its pixels).
            const string Pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
                               "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n";
            var pdf = Path.Combine(_dir, "doc.pdf");
            File.WriteAllText(pdf, Pdf);
            NFTAssetFileUtility.CreateNFTAssetThumbnail(pdf, Path.Combine(_dir, "thumb-doc.pdf"));
            Assert.True(File.Exists(ThumbOf("doc.pdf")));
        }

        [Fact]
        public void AFileNamedPng_ThatIsAnotherFormat_IsNotDecoded()
        {
            var gif = Image("art.png", MagickFormat.Gif); // GIF bytes under a .png name
            NFTAssetFileUtility.CreateNFTAssetThumbnail(gif, Path.Combine(_dir, "thumb-art.png"));
            Assert.False(File.Exists(ThumbOf("art.png")));
        }

        [Fact]
        public void AFormatOutsideThePolicy_IsRefusedEvenWhenNamed()
        {
            var ppm = Path.Combine(_dir, "art.ppm");
            File.WriteAllText(ppm, "P3\n1 1\n255\n255 0 0\n");
            Assert.ThrowsAny<MagickPolicyErrorException>(() => new MagickImage(ppm, new MagickReadSettings { Format = MagickFormat.Ppm }));
        }

        [Fact]
        public void AValidatorNode_MakesNoThumbnails()
        {
            var png = Image("art.png", MagickFormat.Png);
            Globals.ValidatorAddress = "xValidator";
            NFTAssetFileUtility.CreateNFTAssetThumbnail(png, Path.Combine(_dir, "thumb-art.png"));
            Assert.False(File.Exists(ThumbOf("art.png")));
        }
    }
}
