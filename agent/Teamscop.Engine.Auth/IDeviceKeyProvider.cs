namespace Teamscop.Engine.Auth;

public interface IDeviceKeyProvider
{
    /// <summary>
    /// Returns a stable hardware-bound device key (SHA-256 hex).
    /// </summary>
    string GetDeviceKey();
}
