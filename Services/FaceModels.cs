using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>
/// The two small face models People uses, from the OpenCV Model Zoo (hosted on Hugging Face):
///   - YuNet finds faces and their eyes, nose and mouth corners (MIT licence, about 0.2 MB)
///   - SFace turns an aligned face into 128 numbers that are close for the same person
///     (Apache-2.0 licence, about 39 MB)
/// Downloaded once, when the user turns People on.
/// </summary>
public static class FaceModels
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocula", "Models", "faces");

    public const string SizeText = "about 40 MB";

    public static IReadOnlyList<SmartModels.ModelFile> Files => new[]
    {
        new SmartModels.ModelFile("yunet.onnx",
            "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx"),
        new SmartModels.ModelFile("sface.onnx",
            "https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx")
    };

    public static string PathOf(string localName) => Path.Combine(Folder, localName);

    public static bool IsInstalled => Files.All(f => File.Exists(PathOf(f.LocalName)));

    public static Task DownloadAsync(IProgress<double>? progress, CancellationToken cancel) =>
        SmartModels.DownloadFilesAsync(Folder, Files, progress, cancel);

    public static void Delete()
    {
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // In use; removed next time.
        }
    }
}
