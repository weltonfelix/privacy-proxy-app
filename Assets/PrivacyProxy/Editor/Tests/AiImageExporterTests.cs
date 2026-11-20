using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class AiImageExporterTests
{
    [Test]
    public void ExportDrawsWhiteCardAndDarkTextOverNeutralCoverage()
    {
        var photo = new Texture2D(500, 300, TextureFormat.RGBA32, false);
        var pixels = new Color32[500 * 300];
        var mask = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Color32(255, 0, 255, 255);
            mask[i] = true;
        }
        photo.SetPixels32(pixels);
        photo.Apply();
        Texture2D output = null;
        try
        {
            output = AiImageExporter.Compose(photo, mask, "Circuit board");
            bool whiteCard = false, darkText = false, neutralCover = false;
            foreach (var pixel in output.GetPixels32())
            {
                Assert.AreNotEqual(new Color32(255, 0, 255, 255), pixel, "Original photo leaked.");
                whiteCard |= pixel.r == 255 && pixel.g == 255 && pixel.b == 255;
                darkText |= pixel.r < 32 && pixel.g < 32 && pixel.b < 32;
                neutralCover |= Math.Abs(pixel.r - 192) <= 1 && pixel.r == pixel.g && pixel.g == pixel.b;
            }
            Assert.IsTrue(whiteCard, "The white card was not drawn.");
            Assert.IsTrue(darkText, "The description text was not drawn.");
            Assert.IsTrue(neutralCover, "The neutral background was not drawn.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(photo);
            UnityEngine.Object.DestroyImmediate(output);
        }
    }

    [Test]
    public void ExportedPngDoesNotDependOnOriginalObjectPixelsInPocFixture()
    {
        var photo = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/PrivacyProxy/Textures/source.png");
        var maskImage = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/PrivacyProxy/Textures/lower_assembly.png");
        Assert.IsNotNull(photo);
        Assert.IsNotNull(maskImage);
        Assert.AreEqual(photo.width, maskImage.width);
        Assert.AreEqual(photo.height, maskImage.height);
        var original = photo.GetPixels32();
        var mask = MaskContour.MaskFromPng(maskImage);
        Texture2D composed = null;
        Texture2D control = null;
        var alteredPhoto = new Texture2D(photo.width, photo.height, TextureFormat.RGBA32, false);
        var decoded = new Texture2D(2, 2);
        try
        {
            composed = AiImageExporter.Compose(photo, mask, "Circuit board");
            Assert.IsTrue(decoded.LoadImage(composed.EncodeToPNG()));
            Assert.AreEqual(photo.width, decoded.width);
            Assert.AreEqual(photo.height, decoded.height);
            var output = decoded.GetPixels32();
            var alteredPixels = (Color32[])original.Clone();
            var covered = AiImageComposer.CoverObject(original, mask, photo.width, photo.height, out _);
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                Assert.AreEqual(AiImageComposer.CoverColor, covered[i], "Original object pixel was not covered.");
                Assert.AreEqual(255, output[i].a, "Transparent object pixel at index " + i);
                alteredPixels[i] = new Color32((byte)(255 - original[i].r), (byte)(255 - original[i].g),
                    (byte)(255 - original[i].b), 255);
            }
            // White source pixels can equal the white card by coincidence. Instead,
            // change EVERY protected source pixel and require an identical export.
            alteredPhoto.SetPixels32(alteredPixels);
            alteredPhoto.Apply();
            control = AiImageExporter.Compose(alteredPhoto, mask, "Circuit board");
            CollectionAssert.AreEqual(control.GetPixels32(), output,
                "The PNG still depends on pixels of the original object.");
            CollectionAssert.AreEqual(original, photo.GetPixels32(), "The original photo was changed.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(composed);
            UnityEngine.Object.DestroyImmediate(control);
            UnityEngine.Object.DestroyImmediate(alteredPhoto);
            UnityEngine.Object.DestroyImmediate(decoded);
        }
    }

    [Test]
    public void ExportRestoresActiveRenderTextureAndDoesNotKeepTemporaryObjects()
    {
        var photo = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        var originalTarget = RenderTexture.active;
        var target = RenderTexture.GetTemporary(64, 64);
        var pixels = new Color32[64 * 64];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 0, 255, 255);
        photo.SetPixels32(pixels);
        photo.Apply();
        var mask = new bool[pixels.Length];
        mask[0] = true; // exercise clipping the card and margin at the bottom-left corner
        Texture2D output = null;
        try
        {
            RenderTexture.active = target;
            output = AiImageExporter.Compose(photo, mask, "Object");
            Assert.AreSame(target, RenderTexture.active);
            Assert.IsNull(GameObject.Find("Protected image export"));
            var result = output.GetPixels32();
            for (int y = 0; y <= AiImageComposer.DefaultMarginPixels; y++)
            for (int x = 0; x <= AiImageComposer.DefaultMarginPixels; x++)
                Assert.AreNotEqual(pixels[y * 64 + x], result[y * 64 + x], "Object or safety margin leaked.");
        }
        finally
        {
            RenderTexture.active = originalTarget;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.DestroyImmediate(photo);
            UnityEngine.Object.DestroyImmediate(output);
        }
    }

    [Test]
    public void ExportRejectsMissingPhotoAndBlankDescription()
    {
        Assert.Throws<ArgumentNullException>(() => AiImageExporter.Compose(null, new[] { true }, "Object"));
        var photo = new Texture2D(1, 1);
        try
        {
            Assert.Throws<ArgumentException>(() => AiImageExporter.Compose(photo, new[] { true }, "  "));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(photo);
        }
    }
}
