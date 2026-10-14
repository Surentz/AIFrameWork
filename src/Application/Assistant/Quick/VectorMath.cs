namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// The only vector maths the router needs. Plain loops: a few hundred 384-number vectors take
/// microseconds, and Application references no numerics package.
/// </summary>
public static class VectorMath
{
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vectors differ in length: {a.Length} and {b.Length}.", nameof(b));
        }

        var sum = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Scales <paramref name="vector"/> to unit length in place and returns it. A zero vector is left as it is.</summary>
    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var length = Math.Sqrt(Dot(vector, vector));
        if (length == 0)
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / length);
        }

        return vector;
    }
}
