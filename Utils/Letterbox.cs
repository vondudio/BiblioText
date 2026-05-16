namespace AIDevGallery.Sample.Utils;

internal readonly record struct Letterbox(float Scale, int PadX, int PadY)
{
    public static Letterbox Compute(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        float scale = System.Math.Min((float)targetWidth / sourceWidth, (float)targetHeight / sourceHeight);
        int scaledWidth = (int)(sourceWidth * scale);
        int scaledHeight = (int)(sourceHeight * scale);
        int padX = (targetWidth - scaledWidth) / 2;
        int padY = (targetHeight - scaledHeight) / 2;
        return new Letterbox(scale, padX, padY);
    }

    public Box UndoOnBox(float xmin, float ymin, float xmax, float ymax)
    {
        return new Box(
            (xmin - PadX) / Scale,
            (ymin - PadY) / Scale,
            (xmax - PadX) / Scale,
            (ymax - PadY) / Scale);
    }
}
