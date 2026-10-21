namespace LiveCaptionsTranslator.Core.Audio;

/// <summary>
/// A single second-order IIR section. Two of them are enough to keep the resampler honest without
/// making the capture path noticeable.
/// </summary>
public sealed class Biquad
{
    private readonly double b0, b1, b2, a1, a2;
    private double x1, x2, y1, y2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        this.b0 = b0 / a0;
        this.b1 = b1 / a0;
        this.b2 = b2 / a0;
        this.a1 = a1 / a0;
        this.a2 = a2 / a0;
    }

    public static Biquad LowPass(double sampleRate, double cutoff, double q)
    {
        (double cos, double alpha) = Coefficients(sampleRate, cutoff, q);

        return new Biquad(
            (1 - cos) / 2, 1 - cos, (1 - cos) / 2,
            1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad HighPass(double sampleRate, double cutoff, double q)
    {
        (double cos, double alpha) = Coefficients(sampleRate, cutoff, q);

        return new Biquad(
            (1 + cos) / 2, -(1 + cos), (1 + cos) / 2,
            1 + alpha, -2 * cos, 1 - alpha);
    }

    public float Process(float input)
    {
        double x0 = input;
        double y0 = (b0 * x0) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);

        x2 = x1;
        x1 = x0;
        y2 = y1;
        y1 = y0;

        return (float)y0;
    }

    private static (double Cos, double Alpha) Coefficients(double sampleRate, double cutoff, double q)
    {
        double omega = 2 * Math.PI * Math.Min(cutoff, sampleRate / 2 * 0.99) / sampleRate;
        double cos = Math.Cos(omega);
        double alpha = Math.Sin(omega) / (2 * q);
        return (cos, alpha);
    }
}
