using System.Security.Cryptography;
using System.Text;

namespace UnityRuntimeAnalysisAgent.Core.Transport;

/// <summary>The per-start session token: 32 random bytes as 64 lowercase hex characters. Clients must present it in <c>hello</c>.</summary>
public sealed class SessionToken
{
    private readonly byte[] _ascii;

    private SessionToken(string value)
    {
        Value = value;
        _ascii = Encoding.ASCII.GetBytes(value);
    }

    /// <summary>The token (64 hex characters).</summary>
    public string Value { get; }

    /// <summary>Generates a new random token.</summary>
    public static SessionToken Generate()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        var sb = new StringBuilder(64);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return new SessionToken(sb.ToString());
    }

    /// <summary>Compares a presented token in constant time (for equal lengths).</summary>
    public bool Matches(string? presented)
    {
        if (presented is null || presented.Length != Value.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < _ascii.Length; i++)
        {
            diff |= _ascii[i] ^ presented[i];
        }

        return diff == 0;
    }

    /// <inheritdoc />
    public override string ToString() => "<session token>";
}
