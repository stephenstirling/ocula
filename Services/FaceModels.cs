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

    public const string SizeText = "about 39 MB";

    // Pinned to one revision of each repository, with the size and SHA-256 Hugging Face publishes
    // for the file, so a changed or damaged download is rejected (see SmartModels.DownloadFilesAsync).
    //   https://huggingface.co/opencv/face_detection_yunet/tree/3cc26e7f1014a5ee5d74a42acee58bafc9d0a310
    //   https://huggingface.co/opencv/face_recognition_sface/tree/3d7082438a6e4551e840c9b2bb60b71e8da4b524
    public static IReadOnlyList<SmartModels.ModelFile> Files => new[]
    {
        new SmartModels.ModelFile("yunet.onnx",
            "https://huggingface.co/opencv/face_detection_yunet/resolve/3cc26e7f1014a5ee5d74a42acee58bafc9d0a310/face_detection_yunet_2023mar.onnx",
            232_589, "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4"),
        new SmartModels.ModelFile("sface.onnx",
            "https://huggingface.co/opencv/face_recognition_sface/resolve/3d7082438a6e4551e840c9b2bb60b71e8da4b524/face_recognition_sface_2021dec.onnx",
            38_696_353, "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79")
    };

    public static string PathOf(string localName) => Path.Combine(Folder, localName);

    /// <summary>Both files are present with the expected size (hashes are checked when downloading).</summary>
    public static bool IsInstalled => Files.All(f =>
        File.Exists(PathOf(f.LocalName)) && (f.Size <= 0 || new FileInfo(PathOf(f.LocalName)).Length == f.Size));

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
