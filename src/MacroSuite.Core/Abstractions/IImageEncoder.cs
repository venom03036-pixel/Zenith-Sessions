using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Service contract for converting raw <see cref="CaptureFrame"/> pixel buffers into compressed image bytes (JPEG/PNG).
/// Isolated behind an interface to prevent engine coupling to native imaging libraries (Rule 2, Rule 3).
/// </summary>
public interface IImageEncoder
{
    /// <summary>
    /// Encodes a <see cref="CaptureFrame"/> into JPEG-compressed byte array.
    /// </summary>
    /// <param name="frame">The raw pixel frame to encode.</param>
    /// <param name="quality">JPEG compression quality (1-100, default 80).</param>
    /// <returns>Compressed JPEG bytes.</returns>
    byte[] EncodeJpeg(CaptureFrame frame, int quality = 80);

    /// <summary>
    /// Encodes a JPEG, shrinking the long edge to <paramref name="maxEdge"/> when the
    /// frame is larger. Used by AI vision so a full-desktop capture does not encode
    /// tens of megabytes on the UI thread.
    /// </summary>
    byte[] EncodeJpeg(CaptureFrame frame, int quality, int maxEdge)
        => EncodeJpeg(frame, quality, maxEdge, maxEdge);

    /// <summary>
    /// Encodes a JPEG fitted inside <paramref name="maxWidth"/>×<paramref name="maxHeight"/>
    /// (aspect preserved, never upscaled). Pass 0 to skip resizing.
    /// </summary>
    byte[] EncodeJpeg(CaptureFrame frame, int quality, int maxWidth, int maxHeight)
        => EncodeJpeg(frame, quality);

    /// <summary>
    /// Encodes a <see cref="CaptureFrame"/> into PNG-compressed byte array.
    /// </summary>
    /// <param name="frame">The raw pixel frame to encode.</param>
    /// <returns>Compressed PNG bytes.</returns>
    byte[] EncodePng(CaptureFrame frame);

    /// <summary>
    /// Encodes a PNG fitted inside <paramref name="maxWidth"/>×<paramref name="maxHeight"/>.
    /// Pass 0 to skip resizing. Template-matching images should stay native.
    /// </summary>
    byte[] EncodePng(CaptureFrame frame, int maxWidth, int maxHeight)
        => EncodePng(frame);
}
