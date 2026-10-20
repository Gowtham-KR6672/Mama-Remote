using RemoteConnect.Protocol;

namespace RemoteConnect.Session.Media;

public sealed record QualitySettings(int MaxWidth, int MaxHeight, int JpegQuality, int Fps)
{
    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(1000.0 / Fps);

    public static QualitySettings For(QualityPreset preset) => preset switch
    {
        QualityPreset.Speed => new QualitySettings(1366, 768, 45, 15),
        QualityPreset.Quality => new QualitySettings(2560, 1600, 85, 25),
        _ => new QualitySettings(1920, 1080, 65, 20),
    };
}
