using System;
using NUnit.Framework;
using UnityEngine;

public class AiImageComposerTests
{
    static Color32[] Photo(int width, int height)
    {
        var pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32((byte)(i % 127), 50, 80, 255);
        return pixels;
    }

    [Test]
    public void DefaultMarginCoversSixteenPixelsIncludingDiagonals()
    {
        var photo = Photo(41, 41);
        var mask = new bool[photo.Length];
        const int center = 20;
        mask[center * 41 + center] = true;
        var output = AiImageComposer.CoverObject(photo, mask, 41, 41, out var bounds);
        Assert.AreEqual(new RectInt(center, center, 1, 1), bounds);
        for (int y = 0; y < 41; y++)
        for (int x = 0; x < 41; x++)
        {
            bool covered = x >= 4 && x <= 36 && y >= 4 && y <= 36;
            Assert.AreEqual(covered ? AiImageComposer.CoverColor : photo[y * 41 + x], output[y * 41 + x]);
        }
        Assert.AreEqual(new Color32((byte)((center * 41 + center) % 127), 50, 80, 255), photo[center * 41 + center],
            "The original photo was modified.");
        Assert.IsTrue(mask[center * 41 + center]);
    }

    [TestCase(0, 0)]
    [TestCase(6, 0)]
    [TestCase(0, 4)]
    [TestCase(6, 4)]
    public void MarginIsClippedAtEachCornerWithoutWrapping(int objectX, int objectY)
    {
        var photo = Photo(7, 5);
        var mask = new bool[photo.Length];
        mask[objectY * 7 + objectX] = true;
        var output = AiImageComposer.CoverObject(photo, mask, 7, 5, out _, 1);
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 7; x++)
        {
            bool covered = Math.Abs(x - objectX) <= 1 && Math.Abs(y - objectY) <= 1;
            Assert.AreEqual(covered ? AiImageComposer.CoverColor : photo[y * 7 + x], output[y * 7 + x]);
        }
    }

    [Test]
    public void ZeroMarginPreservesHolesAndDisconnectedBackground()
    {
        var photo = Photo(5, 4);
        var mask = new bool[photo.Length];
        mask[0] = mask[19] = true;
        var output = AiImageComposer.CoverObject(photo, mask, 5, 4, out var bounds, 0);
        Assert.AreEqual(new RectInt(0, 0, 5, 4), bounds);
        for (int i = 0; i < mask.Length; i++)
            Assert.AreEqual(mask[i] ? AiImageComposer.CoverColor : photo[i], output[i]);
    }

    [Test]
    public void VeryLargeMarginCoversTheEntireImage()
    {
        var mask = new bool[6];
        mask[0] = true;
        var output = AiImageComposer.CoverObject(Photo(3, 2), mask, 3, 2, out _, int.MaxValue);
        foreach (var pixel in output) Assert.AreEqual(AiImageComposer.CoverColor, pixel);
    }

    [Test]
    public void RejectsEmptyMismatchedAndInvalidInputs()
    {
        var photo = Photo(2, 2);
        var valid = new[] { true, false, false, false };
        Assert.Throws<ArgumentException>(() => AiImageComposer.CoverObject(photo, new bool[4], 2, 2, out _));
        Assert.Throws<ArgumentException>(() => AiImageComposer.CoverObject(photo, new bool[3], 2, 2, out _));
        Assert.Throws<ArgumentException>(() => AiImageComposer.CoverObject(new Color32[3], valid, 2, 2, out _));
        Assert.Throws<ArgumentNullException>(() => AiImageComposer.CoverObject(null, valid, 2, 2, out _));
        Assert.Throws<ArgumentNullException>(() => AiImageComposer.CoverObject(photo, null, 2, 2, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiImageComposer.CoverObject(photo, valid, 0, 2, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiImageComposer.CoverObject(photo, valid, int.MaxValue, 2, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiImageComposer.CoverObject(photo, valid, 2, 2, out _, -1));
    }

    [TestCase(1, 7, 0)]
    [TestCase(9, 1, 3)]
    [TestCase(9, 7, 1)]
    [TestCase(9, 7, 3)]
    [TestCase(9, 7, 16)]
    public void DilationMatchesIndependentDistanceCheck(int width, int height, int margin)
    {
        var random = new System.Random(7);
        var photo = Photo(width, height);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var mask = new bool[photo.Length];
            for (int i = 0; i < mask.Length; i++) mask[i] = random.Next(5) == 0;
            mask[random.Next(mask.Length)] = true;
            var output = AiImageComposer.CoverObject(photo, mask, width, height, out _, margin);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool covered = false;
                for (int my = 0; my < height; my++)
                for (int mx = 0; mx < width; mx++)
                    if (mask[my * width + mx] && Math.Abs(x - mx) <= margin && Math.Abs(y - my) <= margin)
                        covered = true;
                Assert.AreEqual(covered ? AiImageComposer.CoverColor : photo[y * width + x], output[y * width + x]);
            }
        }
    }
}
