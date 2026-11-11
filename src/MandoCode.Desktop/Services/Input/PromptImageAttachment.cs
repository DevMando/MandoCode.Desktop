using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>A draft image, never queued in the engine until its own user turn is claimed.</summary>
public sealed class PromptImageAttachment
{
    public string Name { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
    public string MediaType { get; }
    // The engine queue holds eight content blocks; each named image uses a caption and bytes.
    public const int MaxImages = 4;
    public const int MaxBytes = AIService.MaxImageInputBytes;
    public const ulong MaxPixels = 16_000_000;

    private PromptImageAttachment(string name, byte[] bytes, string mediaType)
    {
        Name = name;
        Bytes = bytes;
        MediaType = mediaType;
    }

    public static PromptImageAttachment Create(string name, byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidDataException("The image is empty.");
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Use an image no larger than 4 MiB.");
        ReadOnlySpan<byte> data = bytes;
        var type = data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255 ? "image/jpeg"
            : data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
            : data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
            : throw new InvalidDataException("Choose a valid PNG, JPEG, GIF, or WebP image.");
        return new(name, bytes.ToArray(), type);
    }

    public static string? SubmissionError(string input, ModelVisionSupport vision, bool connected, bool modelError, int imageCount = 1)
    {
        if (imageCount < 1 || imageCount > MaxImages) return "Attach between one and four images per message.";
        if (input.TrimStart().StartsWith('/') || input.TrimStart().StartsWith('!'))
            return "Images go with chat messages. Send a message, or remove the image before running a command.";
        if (!connected || modelError) return "Connect to an available vision model before sending this image.";
        return vision switch
        {
            ModelVisionSupport.Supported => null,
            ModelVisionSupport.Unsupported => "This model is text-only. Choose a vision model or remove the image.",
            _ => "Image support is not confirmed for this model. Choose a confirmed vision model or remove the image."
        };
    }

    public static string MessageText(string text, int imageCount = 1) => string.IsNullOrWhiteSpace(text)
        ? imageCount == 1 ? "Describe the attached image." : "Describe the attached images." : text;
}
