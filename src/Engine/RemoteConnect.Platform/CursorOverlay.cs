namespace RemoteConnect.Platform;

/// <summary>Draws a standard arrow pointer into a BGRA frame (used where the OS capture omits the cursor).</summary>
public static class CursorOverlay
{
    // 0 = transparent, 1 = black outline, 2 = white fill. 12 x 19 classic arrow, hotspot at (0,0).
    private static readonly string[] Shape =
    {
        "1...........",
        "11..........",
        "121.........",
        "1221........",
        "12221.......",
        "122221......",
        "1222221.....",
        "12222221....",
        "122222221...",
        "1222222221..",
        "12222222221.",
        "122222111111",
        "1221221.....",
        "121.1221....",
        "11..1221....",
        "1....1221...",
        ".....1221...",
        "......1221..",
        "......111...",
    };

    public static void Draw(FrameBuffer frame, int x, int y)
    {
        var px = frame.Pixels;
        for (int row = 0; row < Shape.Length; row++)
        {
            int ty = y + row;
            if (ty < 0 || ty >= frame.Height)
            {
                continue;
            }

            var line = Shape[row];
            for (int col = 0; col < line.Length; col++)
            {
                int tx = x + col;
                char c = line[col];
                if (c == '.' || tx < 0 || tx >= frame.Width)
                {
                    continue;
                }

                byte v = c == '1' ? (byte)0 : (byte)255;
                int o = (ty * frame.Stride) + (tx * 4);
                px[o] = v;
                px[o + 1] = v;
                px[o + 2] = v;
                px[o + 3] = 255;
            }
        }
    }
}
