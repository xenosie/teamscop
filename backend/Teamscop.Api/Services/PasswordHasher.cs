using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Teamscop.Api.Services;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string encodedHash);
}

public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int DegreeOfParallelism = 2;
    private const int MemorySizeKb = 32 * 1024;
    private const int Iterations = 3;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = HashCore(password, salt);
        return $"argon2id${Iterations}${MemorySizeKb}${DegreeOfParallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6 || parts[0] != "argon2id")
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) ||
            !int.TryParse(parts[2], out var memory) ||
            !int.TryParse(parts[3], out var parallelism))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[4]);
        var expected = Convert.FromBase64String(parts[5]);
        var actual = HashCore(password, salt, iterations, memory, parallelism);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] HashCore(
        string password,
        byte[] salt,
        int iterations = Iterations,
        int memorySizeKb = MemorySizeKb,
        int degreeOfParallelism = DegreeOfParallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = degreeOfParallelism,
            MemorySize = memorySizeKb,
            Iterations = iterations
        };
        return argon2.GetBytes(HashSize);
    }
}
