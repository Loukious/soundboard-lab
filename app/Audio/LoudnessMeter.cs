namespace SoundboardLab.Audio;

/// <summary>ITU-R BS.1770 K weighting at 48 kHz; 400 ms blocks, 75% overlap, -70 LUFS and -10 LU relative gates.</summary>
public static class LoudnessMeter
{
    public static double Measure(float[] stereo48k)
    {
        if (stereo48k.Length % 2 != 0) throw new ArgumentException("Expected stereo samples.");
        var frames = stereo48k.Length / 2;
        if (frames < 19200) return double.NegativeInfinity; // No complete R128 measurement block.
        var sums = new double[frames + 1];
        var shelf = new[] { new Filter(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585), new Filter(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585) };
        var highpass = new[] { new Filter(1, -2, 1, -1.99004745483398, 0.99007225036621), new Filter(1, -2, 1, -1.99004745483398, 0.99007225036621) };
        for (var f = 0; f < frames; f++)
        {
            var l = highpass[0].Process(shelf[0].Process(stereo48k[f * 2]));
            var r = highpass[1].Process(shelf[1].Process(stereo48k[f * 2 + 1]));
            sums[f + 1] = sums[f] + l * l + r * r;
        }
        var energies = new List<double>();
        for (var start = 0; start + 19200 <= frames; start += 4800)
        {
            var energy = (sums[start + 19200] - sums[start]) / 19200;
            if (ToLufs(energy) >= -70) energies.Add(energy);
        }
        if (energies.Count == 0) return double.NegativeInfinity;
        var relativeGate = ToLufs(energies.Average()) - 10;
        var gated = energies.Where(e => ToLufs(e) >= relativeGate).ToArray();
        return gated.Length == 0 ? double.NegativeInfinity : ToLufs(gated.Average());
    }
    private static double ToLufs(double energy) => energy > 0 ? -0.691 + 10 * Math.Log10(energy) : double.NegativeInfinity;
    private sealed class Filter(double b0, double b1, double b2, double a1, double a2)
    {
        private double z1, z2;
        public double Process(double sample)
        {
            var y = b0 * sample + z1;
            z1 = b1 * sample - a1 * y + z2;
            z2 = b2 * sample - a2 * y;
            return y;
        }
    }
}
