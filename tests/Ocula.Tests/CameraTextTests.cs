using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>Looking up a photo of the camera or phone, and shutter speeds (CameraText).</summary>
public class CameraTextTests
{
    [Fact]
    public void A_new_phone_is_also_searched_by_its_base_model_but_never_without_the_number()
    {
        Assert.Equal(new[]
        {
            "Google Pixel 10 Pro XL", "Pixel 10 Pro XL", "Google Pixel 10 Pro",
            "Pixel 10 Pro", "Google Pixel 10", "Pixel 10",
        }, CameraText.SearchNames("Google Pixel 10 Pro XL"));
    }

    [Theory]
    [InlineData("Canon EOS R5", new[] { "Canon EOS R5", "EOS R5" })]
    [InlineData("SONY ILCE-7M4", new[] { "SONY ILCE-7M4", "ILCE-7M4" })]
    [InlineData("Apple iPhone 15 Pro", new[] { "Apple iPhone 15 Pro", "iPhone 15 Pro", "Apple iPhone 15", "iPhone 15" })]
    public void Camera_search_names(string camera, string[] names) =>
        Assert.Equal(names, CameraText.SearchNames(camera));

    [Theory]
    [InlineData("Google Pixel 10 Pro XL", "10")]
    [InlineData("Canon EOS R5", "R5")]
    [InlineData("SONY ILCE-7M4", "ILCE-7M4")]
    [InlineData("Ricoh GR", null)]
    public void The_model_number_is_the_first_word_with_a_digit(string camera, string? number) =>
        Assert.Equal(number, CameraText.ModelNumber(camera));

    [Theory]
    [InlineData("Pixel 10", "10", true)]
    [InlineData("Google Pixel 10 Pro XL", "10", true)]
    [InlineData("Pixel 9", "10", false)]
    [InlineData("Pixel 10a", "10", false)]
    [InlineData("Canon EOS R5", "r5", true)]
    [InlineData("Anything", null, true)]
    public void A_result_must_mention_the_model_number(string title, string? number, bool mentions) =>
        Assert.Equal(mentions, CameraText.Mentions(title, number));

    [Theory]
    [InlineData("Android smartphone by Google", true)]
    [InlineData("smartphone model", true)]
    [InlineData("mirrorless interchangeable-lens camera", true)]
    [InlineData("quadcopter drone", true)]
    [InlineData("2010 film directed by Joe Smith", false)]
    [InlineData(null, false)]
    public void Only_devices_that_take_photos_count(string? description, bool device) =>
        Assert.Equal(device, CameraText.IsDevice(description));

    [Theory]
    [InlineData(0.009539, "1/105 sec")]
    [InlineData(1.0 / 250, "1/250 sec")]
    [InlineData(1.0 / 3, "1/3 sec")]
    [InlineData(0.4, "0.4 sec")]
    [InlineData(1.5, "1.5 sec")]
    [InlineData(30, "30 sec")]
    [InlineData(0, null)]
    public void Shutter_speeds_read_like_a_camera(double seconds, string? text) =>
        Assert.Equal(text, CameraText.Shutter(seconds));

    [Fact]
    public void Raw_fractions_saved_in_the_library_are_tidied()
    {
        Assert.Equal("2 mm · f/1.7 · 1/105 sec · ISO 192", CameraText.TidyExposure("2 mm · f/1.7 · 9539/1000000 sec · ISO 192"));
        Assert.Equal("50 mm · f/2.8 · 1/250 sec · ISO 100", CameraText.TidyExposure("50 mm · f/2.8 · 1/250 sec · ISO 100"));
    }
}
