namespace ContentFactory.Infrastructure.Media;

/// <summary>
/// Product-basic v1 template constants. These values are part of the render identity: a
/// different canvas, font or caption policy must be a new template/renderer version.
/// </summary>
public static class ProductBasicTemplate
{
    public const string Version = "product-basic.v1";
    public const string RendererVersion = "ffmpeg-native-v1";
    public const string FontName = "Thonburi UI";

    public const int Width = 1080;
    public const int Height = 1920;
    public const int Fps = 30;

    public const int SafeLeft = 80;
    public const int SafeRight = 880;
    public const int SafeTop = 180;
    public const int SafeBottom = 1500;

    public const int CaptionFontSize = 56;
    public const int CaptionMaxLines = 2;
    public const int CaptionMaxWidth = SafeRight - SafeLeft;

    public const long MinDurationMs = 15_000;
    public const long MaxDurationMs = 35_000;
    public const long CaptionPaddingMs = 100;

    public const string VideoBitrate = "6M";
    public const string AudioBitrate = "128k";
    public const int AudioSampleRate = 48_000;
    public const int AudioChannels = 2;
    public const int ThumbnailFrameMs = 1_000;
}
