using System;
using ImageMagick;
using Ocula.Models;

namespace Ocula.Services;

public sealed record ColourProfile(byte[] Icc, string Name);

/// <summary>
/// Reads a photo's embedded colour profile. Only non-sRGB profiles matter: sRGB photos already
/// show correctly, but Display P3 (most phones), Adobe RGB and ProPhoto (cameras, editors) look
/// dull or oversaturated unless converted for the screen.
/// </summary>
public static class ColourService
{
    public static ColourProfile? ReadWideGamutProfile(ImageItem item)
    {
        if (item.IsVideo || item.IsCloudOnly || SupportedFormats.IsRaw(item.Path)) return null;
        try
        {
            using var image = new MagickImage();
            image.Ping(item.Path); // reads metadata only, not the pixels
            var profile = image.GetColorProfile();
            if (profile == null) return null;

            var name = (profile.Description ?? profile.Model ?? "Embedded profile").Trim();
            if (name.Contains("sRGB", StringComparison.OrdinalIgnoreCase)) return null;
            return new ColourProfile(profile.ToByteArray(), name);
        }
        catch
        {
            return null;
        }
    }
}
