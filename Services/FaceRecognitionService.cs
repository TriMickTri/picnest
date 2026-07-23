using System.Net.Http;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using PicNest.Models;

namespace PicNest.Services;

/// <summary>
/// Runs face detection and face embedding generation entirely on this computer. The two public
/// OpenCV models are cached locally once; image pixels are never sent to a service.
/// </summary>
public sealed class FaceRecognitionService : IDisposable
{
    private const int DetectionSize = 640;
    private const string DetectorFileName = "face_detection_yunet_2023mar.onnx";
    private const string RecognitionFileName = "face_recognition_sface_2021dec.onnx";
    private const string DetectorModelUrl =
        "https://github.com/opencv/opencv_zoo/raw/refs/heads/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx";
    private const string RecognitionModelUrl =
        "https://github.com/opencv/opencv_zoo/raw/refs/heads/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx";

    private static readonly HttpClient DownloadClient = new();
    private FaceDetectorYN? _detector;
    private Net? _recognizer;
    private bool _disposed;

    public async Task EnsureReadyAsync(Action<string>? status, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        LibraryPaths.EnsureCreated();
        var detectorPath = await EnsureModelAsync(DetectorFileName, DetectorModelUrl, "face detector", status, cancellationToken);
        var recognizerPath = await EnsureModelAsync(RecognitionFileName, RecognitionModelUrl, "face recognition model", status, cancellationToken);

        _detector ??= FaceDetectorYN.Create(detectorPath, "", new Size(DetectionSize, DetectionSize), 0.85f, 0.3f, 5000);
        _recognizer ??= CvDnn.ReadNetFromOnnx(recognizerPath);
    }

    internal Task<IReadOnlyList<FaceDetection>> DetectAsync(PhotoRecord photo, CancellationToken cancellationToken) =>
        Task.Run(() => Detect(photo, cancellationToken), cancellationToken);

    private IReadOnlyList<FaceDetection> Detect(PhotoRecord photo, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_detector is null || _recognizer is null)
            throw new InvalidOperationException("Face recognition models have not been initialized.");

        cancellationToken.ThrowIfCancellationRequested();
        using var source = Cv2.ImRead(photo.Path, ImreadModes.Color);
        if (source.Empty()) throw new InvalidDataException("PicNest could not decode this image for face recognition.");

        // The installed .NET wrapper uses the detector's configured input size. Every image is
        // normalized to that size before detection, then landmarks align each face for SFace.
        using var detectionImage = new Mat();
        Cv2.Resize(source, detectionImage, new Size(DetectionSize, DetectionSize), 0, 0, InterpolationFlags.Linear);
        using var faces = new Mat();
        _detector.Detect(detectionImage, faces);

        var results = new List<FaceDetection>();
        for (var index = 0; index < faces.Rows; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var confidence = faces.At<float>(index, 14);
            if (confidence < 0.85f) continue;

            var embedding = ExtractEmbedding(detectionImage, faces, index);
            var preview = CreateFacePreview(detectionImage, faces, index, photo.Hash);
            results.Add(new FaceDetection(confidence, embedding, preview));
        }
        return results;
    }

    private float[] ExtractEmbedding(Mat image, Mat faces, int index)
    {
        var sourceLandmarks = new[]
        {
            new Point2f(faces.At<float>(index, 4), faces.At<float>(index, 5)),
            new Point2f(faces.At<float>(index, 6), faces.At<float>(index, 7)),
            new Point2f(faces.At<float>(index, 8), faces.At<float>(index, 9))
        };
        var destinationLandmarks = new[]
        {
            new Point2f(38.2946f, 51.6963f),
            new Point2f(73.5318f, 51.5014f),
            new Point2f(56.0252f, 71.7366f)
        };

        using var transform = Cv2.GetAffineTransform(sourceLandmarks, destinationLandmarks);
        using var alignedFace = new Mat();
        Cv2.WarpAffine(image, alignedFace, transform, new Size(112, 112), InterpolationFlags.Linear);
        using var blob = CvDnn.BlobFromImage(alignedFace, 1, new Size(112, 112), new Scalar(0, 0, 0), true, false);
        _recognizer!.SetInput(blob);
        using var feature = _recognizer.Forward("");
        using var row = feature.Reshape(1, 1);
        row.GetArray(out float[] embedding);
        return Normalize(embedding);
    }

    private static string CreateFacePreview(Mat image, Mat faces, int index, string photoHash)
    {
        var path = Path.Combine(LibraryPaths.FaceThumbnails, $"{photoHash}-{index}.jpg");
        if (File.Exists(path)) return path;

        var x = faces.At<float>(index, 0);
        var y = faces.At<float>(index, 1);
        var width = faces.At<float>(index, 2);
        var height = faces.At<float>(index, 3);
        var padding = Math.Max(width, height) * 0.22f;
        var left = Math.Max(0, (int)Math.Floor(x - padding));
        var top = Math.Max(0, (int)Math.Floor(y - padding));
        var right = Math.Min(image.Width, (int)Math.Ceiling(x + width + padding));
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(y + height + padding));
        var bounds = new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));

        using var face = new Mat(image, bounds);
        using var preview = new Mat();
        Cv2.Resize(face, preview, new Size(160, 160), 0, 0, InterpolationFlags.Linear);
        Cv2.ImWrite(path, preview, [(int)ImwriteFlags.JpegQuality, 88]);
        return path;
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(value => value * value));
        if (magnitude <= double.Epsilon) throw new InvalidDataException("The face recognition model returned an empty embedding.");
        return vector.Select(value => (float)(value / magnitude)).ToArray();
    }

    private static async Task<string> EnsureModelAsync(string fileName, string url, string displayName,
        Action<string>? status, CancellationToken cancellationToken)
    {
        var modelPath = Path.Combine(LibraryPaths.FaceModels, fileName);
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 1024) return modelPath;

        status?.Invoke($"Downloading the local {displayName} once…");
        var temporaryPath = modelPath + ".download";
        try
        {
            using var response = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(temporaryPath);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            File.Move(temporaryPath, modelPath, overwrite: true);
            return modelPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FaceRecognitionService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detector?.Dispose();
        _recognizer?.Dispose();
    }
}
