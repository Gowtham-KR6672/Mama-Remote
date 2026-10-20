namespace RemoteConnect.Security;

/// <summary>Loads the persisted device identity or creates one on first run.</summary>
public static class DeviceIdentityStore
{
    public static DeviceIdentity LoadOrCreate(string path)
    {
        var existing = ProtectedFileStore.TryRead(path);
        if (existing is not null)
        {
            try
            {
                return DeviceIdentity.FromPkcs12(existing);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // fall through and regenerate
            }
        }

        var identity = DeviceIdentity.Create();
        ProtectedFileStore.Write(path, identity.ExportPkcs12());
        return identity;
    }

    /// <summary>Discards the current key pair (new device ID). Used on ID conflicts or explicit key rotation.</summary>
    public static DeviceIdentity Regenerate(string path)
    {
        var identity = DeviceIdentity.Create();
        ProtectedFileStore.Write(path, identity.ExportPkcs12());
        return identity;
    }
}
