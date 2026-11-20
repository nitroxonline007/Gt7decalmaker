namespace GT7DecalMaker.Convert;

// Converter dials in one place. Defaults are the values picked by the tuning gym
// (GT7DecalMaker.Tests: `gym`), which scores conversions of a sample set
// against the originals and keeps whatever looks closest on average.
public static class Tunables
{
    // Color matching: extra weight on tint differences (keeps silver from turning green).
    public static double TintWeight = 6;
    // Despeckle sizes (px) the auto-fit search tries.
    public static int[] DespeckleSet = { 4, 8, 16, 32 };
    // Edge smoothing range the auto-fit bisects within.
    public static double EpsMin = 0.5, EpsMax = 2.5;
    // Small shapes get smoothing capped at (smaller side x this).
    public static double DetailScale = 0;
    // Detail layer (sharp lettering).
    public static double DetailMinContrast = 70;
    public static double DetailEps = 0.6;
    public static double DetailPurity = 0.8;
    public static int DetailDespeckle = 3;
}
