namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// Abstraction for DHT mutable item signing (BEP 44/46).
/// Implementations can use any signing algorithm:
/// - ECDSA-P256 (WebCrypto native, via SpawnDev.BlazorJS.Cryptography)
/// - Ed25519 (BEP 46 standard, via NSec or .NET built-in when available)
/// - Any future algorithm
///
/// The interface is algorithm-agnostic — callers don't need to know
/// which algorithm is used. Key pairs are opaque byte arrays.
/// </summary>
public interface IDhtSigner
{
    /// <summary>Algorithm name (e.g., "ECDSA-P256", "Ed25519").</summary>
    string Algorithm { get; }

    /// <summary>Public key bytes (used as DHT identity).</summary>
    byte[] PublicKey { get; }

    /// <summary>Sign a message. Returns the signature bytes.</summary>
    Task<byte[]> SignAsync(byte[] message);

    /// <summary>Verify a signature against a public key and message.</summary>
    Task<bool> VerifyAsync(byte[] publicKey, byte[] message, byte[] signature);

    /// <summary>Export the key pair for persistence.</summary>
    Task<(byte[] publicKey, byte[] privateKey)> ExportKeyPairAsync();
}

/// <summary>
/// HMAC-SHA512 fallback signer. NOT cryptographically secure for real use —
/// only for testing and when no real crypto provider is available.
/// </summary>
[Obsolete("Use EcdsaP256Signer with IPortableCrypto for real signing. HMAC is not a signature scheme.")]
public class HmacFallbackSigner : IDhtSigner
{
    private readonly byte[] _privateKey;
    private readonly byte[] _publicKey;

    public string Algorithm => "HMAC-SHA512-Fallback";
    public byte[] PublicKey => _publicKey;

    public HmacFallbackSigner()
    {
        _privateKey = new byte[64];
        _publicKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(_privateKey);
        System.Security.Cryptography.SHA256.HashData(_privateKey.AsSpan(0, 32)).CopyTo(_publicKey);
    }

    public HmacFallbackSigner(byte[] privateKey, byte[] publicKey)
    {
        _privateKey = privateKey;
        _publicKey = publicKey;
    }

    public Task<byte[]> SignAsync(byte[] message)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA512(_privateKey);
        return Task.FromResult(hmac.ComputeHash(message));
    }

    public Task<bool> VerifyAsync(byte[] publicKey, byte[] message, byte[] signature)
    {
        // HMAC verify: recompute and compare
        // Note: this signer is for testing only — real deployments should use EcdsaP256Signer
        if (signature.Length < 64) return Task.FromResult(false);
        using var hmac = new System.Security.Cryptography.HMACSHA512(_privateKey);
        var expected = hmac.ComputeHash(message);
        return Task.FromResult(expected.AsSpan().SequenceEqual(signature.AsSpan(0, expected.Length)));
    }

    public Task<(byte[] publicKey, byte[] privateKey)> ExportKeyPairAsync()
    {
        return Task.FromResult((_publicKey.ToArray(), _privateKey.ToArray()));
    }
}

/// <summary>
/// ECDSA-P256 signer using SpawnDev.BlazorJS.Cryptography.
/// Works in both browser (WebCrypto) and desktop (.NET).
/// This is the RECOMMENDED signer for SpawnDev projects.
/// Note: Not BEP 44 compliant (BEP 44 requires Ed25519), but provides
/// real cryptographic security via cross-platform WebCrypto/System.Security.
/// </summary>
public class EcdsaP256Signer : IDhtSigner
{
    private readonly SpawnDev.BlazorJS.Cryptography.IPortableCrypto _crypto;
    private SpawnDev.BlazorJS.Cryptography.PortableECDSAKey? _key;
    private byte[] _publicKey = new byte[32];

    public string Algorithm => "ECDSA-P256";
    public byte[] PublicKey => _publicKey;

    public EcdsaP256Signer(SpawnDev.BlazorJS.Cryptography.IPortableCrypto crypto)
    {
        _crypto = crypto;
    }

    /// <summary>Generate a new key pair. Must be called before Sign/Verify.</summary>
    public async Task GenerateKeyAsync()
    {
        _key = await _crypto.GenerateECDSAKey("P-256", extractable: true);
        var spki = await _crypto.ExportPublicKeySpki(_key);
        System.Security.Cryptography.SHA256.HashData(spki).CopyTo(_publicKey.AsSpan());
    }

    /// <summary>Import an existing key pair.</summary>
    public async Task ImportKeyAsync(byte[] publicKeySpki, byte[] privateKeyPkcs8)
    {
        _key = await _crypto.ImportECDSAKey(publicKeySpki, privateKeyPkcs8, "P-256", extractable: true);
        System.Security.Cryptography.SHA256.HashData(publicKeySpki).CopyTo(_publicKey.AsSpan());
    }

    public async Task<byte[]> SignAsync(byte[] message)
    {
        if (_key == null) throw new InvalidOperationException("Key not generated. Call GenerateKeyAsync first.");
        var sig = await _crypto.Sign(_key, message, "SHA-256");
        // Pad to 64 bytes for BEP 44 wire format
        var result = new byte[64];
        Array.Copy(sig, result, Math.Min(sig.Length, 64));
        return result;
    }

    public async Task<bool> VerifyAsync(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            // Import the peer's public key and verify the signature
            var peerKey = await _crypto.ImportECDSAKey(publicKey, "P-256");
            return await _crypto.Verify(peerKey, message, signature, "SHA-256");
        }
        catch
        {
            return false; // Invalid key or signature format
        }
    }

    public async Task<(byte[] publicKey, byte[] privateKey)> ExportKeyPairAsync()
    {
        if (_key == null) throw new InvalidOperationException("Key not generated.");
        var pub = await _crypto.ExportPublicKeySpki(_key);
        var priv = await _crypto.ExportPrivateKeyPkcs8(_key);
        return (pub, priv);
    }
}

/// <summary>
/// Ed25519 signer using SpawnDev.BlazorJS.Cryptography 3.1.0+.
/// Works in both browser (WebCrypto) and desktop (.NET).
/// BEP 44 compliant — Ed25519 is the required algorithm for DHT mutable items.
/// This is the RECOMMENDED signer for all DHT signing operations.
/// </summary>
public class Ed25519Signer : IDhtSigner
{
    private readonly SpawnDev.BlazorJS.Cryptography.IPortableCrypto _crypto;
    private SpawnDev.BlazorJS.Cryptography.PortableEd25519Key? _key;
    private byte[] _publicKey = Array.Empty<byte>();

    public string Algorithm => "Ed25519";
    public byte[] PublicKey => _publicKey;

    public Ed25519Signer(SpawnDev.BlazorJS.Cryptography.IPortableCrypto crypto)
    {
        _crypto = crypto;
    }

    /// <summary>Generate a new Ed25519 key pair.</summary>
    public async Task GenerateKeyAsync()
    {
        _key = await _crypto.GenerateEd25519Key(extractable: true);
        _publicKey = await _crypto.ExportPublicKeySpki(_key);
    }

    /// <summary>Import an existing Ed25519 key pair.</summary>
    public async Task ImportKeyAsync(byte[] publicKeySpki, byte[] privateKeyPkcs8)
    {
        _key = await _crypto.ImportEd25519Key(publicKeySpki, privateKeyPkcs8, extractable: true);
        _publicKey = publicKeySpki;
    }

    public async Task<byte[]> SignAsync(byte[] message)
    {
        if (_key == null) throw new InvalidOperationException("Key not generated. Call GenerateKeyAsync first.");
        return await _crypto.Sign(_key, message);
    }

    public async Task<bool> VerifyAsync(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            var peerKey = await _crypto.ImportEd25519Key(publicKey, extractable: false);
            return await _crypto.Verify(peerKey, message, signature);
        }
        catch
        {
            return false;
        }
    }

    public async Task<(byte[] publicKey, byte[] privateKey)> ExportKeyPairAsync()
    {
        if (_key == null) throw new InvalidOperationException("Key not generated.");
        var pub = await _crypto.ExportPublicKeySpki(_key);
        var priv = await _crypto.ExportPrivateKeyPkcs8(_key);
        return (pub, priv);
    }
}
