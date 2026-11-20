using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class ObjectCropTests
{
    [Test]
    public void CropsToInclusiveBoundsPreservesRowsAndFillsHolesWithRgb127()
    {
        var photo = new Color32[6 * 5];
        for (int i = 0; i < photo.Length; i++) photo[i] = new Color32((byte)i, 40, 80, 255);
        var mask = new bool[photo.Length];
        mask[8] = mask[10] = mask[21] = true;
        var before = (Color32[])photo.Clone();
        var originalMask = (bool[])mask.Clone();
        var crop = ObjectCrop.Create(photo, mask, 6, 5);
        var gray = new Color32(127, 127, 127, 255);
        Assert.AreEqual(new RectInt(2, 1, 3, 3), crop.Bounds);
        CollectionAssert.AreEqual(new[] { photo[8], gray, photo[10], gray, gray, gray, gray, photo[21], gray }, crop.Pixels);
        CollectionAssert.AreEqual(before, photo);
        CollectionAssert.AreEqual(originalMask, mask);
    }

    [Test]
    public void CropAtImageEdgesAndSinglePixelIsOpaqueRgb()
    {
        var photo = new[] { new Color32(20, 30, 40, 0), new Color32(50, 60, 70, 90) };
        var crop = ObjectCrop.Create(photo, new[] { false, true }, 2, 1);
        Assert.AreEqual(new RectInt(1, 0, 1, 1), crop.Bounds);
        Assert.AreEqual(new Color32(50, 60, 70, 255), crop.Pixels[0]);
        Assert.AreEqual(90, photo[1].a);
    }

    [Test]
    public void RejectsNullEmptyMismatchedAndOverflowingDimensions()
    {
        var photo = new Color32[4];
        Assert.Throws<ArgumentNullException>(() => ObjectCrop.Create(null, new bool[4], 2, 2));
        Assert.Throws<ArgumentNullException>(() => ObjectCrop.Create(photo, null, 2, 2));
        Assert.Throws<ArgumentException>(() => ObjectCrop.Create(photo, new bool[4], 2, 2));
        Assert.Throws<ArgumentException>(() => ObjectCrop.Create(photo, new bool[3], 2, 2));
        Assert.Throws<ArgumentException>(() => ObjectCrop.Create(photo, new bool[4], 3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectCrop.Create(photo, new bool[4], 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectCrop.Create(photo, new bool[4], int.MaxValue, 2));
    }

    [Test]
    public void PocFixtureAndEncodedPngMatchReferencePixelForPixel()
    {
        var photo = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/PrivacyProxy/Textures/source.png");
        var maskImage = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/PrivacyProxy/Textures/lower_assembly.png");
        var referenceBytes = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/PrivacyProxy/TestData/lower_assembly-crop.png.bytes");
        Assert.IsNotNull(photo);
        Assert.IsNotNull(maskImage);
        Assert.IsNotNull(referenceBytes);
        Assert.AreEqual(photo.width, maskImage.width);
        Assert.AreEqual(photo.height, maskImage.height);
        var crop = ObjectCrop.Create(photo.GetPixels32(), MaskContour.MaskFromPng(maskImage), photo.width, photo.height);
        var expected = new Texture2D(2, 2);
        var generated = new Texture2D(crop.Width, crop.Height, TextureFormat.RGBA32, false);
        var decoded = new Texture2D(2, 2);
        try
        {
            Assert.IsTrue(expected.LoadImage(referenceBytes.bytes));
            Assert.AreEqual(expected.width, crop.Width);
            Assert.AreEqual(expected.height, crop.Height);
            CollectionAssert.AreEqual(expected.GetPixels32(), crop.Pixels);
            generated.SetPixels32(crop.Pixels);
            generated.Apply();
            Assert.IsTrue(decoded.LoadImage(generated.EncodeToPNG()));
            CollectionAssert.AreEqual(expected.GetPixels32(), decoded.GetPixels32());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(expected);
            UnityEngine.Object.DestroyImmediate(generated);
            UnityEngine.Object.DestroyImmediate(decoded);
        }
    }
}
