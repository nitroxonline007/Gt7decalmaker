namespace GT7DecalMaker.Convert;

public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; } // BGRA

    public PixelBuffer(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

public sealed class ConvertSettings
{
    public double Epsilon { get; set; } = 3.0;
    public int MaxColors { get; set; } = 16;
    public int DespecklePx { get; set; } = 4;
    public bool SeamFill { get; set; } = true;
    public bool RemoveFlatBackground { get; set; } = true;
    public bool Stacked { get; set; } = true;
    // Keep small specks whose brightness differs from their surroundings by this much (0 = off).
    public double ProtectContrast { get; set; } = 0;
    // Max edge tolerance for a shape = its smaller side x this (0 = off).
    public double DetailScale { get; set; } = Tunables.DetailScale;
    // Spend part of the budget on a full-res detail layer so lettering stays readable.
    public bool KeepText { get; set; } = true;
    public List<(byte R, byte G, byte B)>? PaletteOverride { get; set; }
}

public sealed class LayerResult
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public List<List<Point>> Loops { get; } = new();

    public LayerResult(byte r, byte g, byte b)
    {
        R = r; G = g; B = b;
    }
}

public sealed class ConvertResult
{
    public string Xml { get; set; } = "";
    public int Bytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int ColorsUsed { get; set; }
    public int LoopCount { get; set; }
    public ConvertSettings Settings { get; set; } = new();
    public List<LayerResult> Layers { get; set; } = new();
    internal ConvertEngine.Traced? Raw { get; set; }
    // Words (2+ letters) redrawn sharp by the detail layer.
    public int DetailWords { get; set; }
}

public readonly record struct Point(double X, double Y);
