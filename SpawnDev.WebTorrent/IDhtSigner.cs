using SpawnDev.BlazorJS.Cryptography;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Abstraction for DHT mutable item signing (BEP 44/46).
/// BEP 44 REQUIRES Ed25519 — use Ed25519Signer for all production DHT operations.
/// </summary>
public interface IDhtSigner
{
    /// <summary>Algorithm name (e.g., "Ed25519").</summary>
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
/// Ed25519 signer using SpawnDev.BlazorJS.Cryptography 3.1.0+.
/// Works in both browser (WebCrypto) and desktop (.NET).
/// BEP 44 compliant — Ed25519 is the REQUIRED algorithm for DHT mutable items.
/// 32-byte public keys, 64-byte signatures, fixed SHA-512 hash.
/// </summary>
public class Ed25519Signer : IDhtSigner
{
    private readonly IPortableCrypto _crypto;
    private PortableEd25519Key? _key;
    private byte[] _publicKey = Array.Empty<byte>();

    public string Algorithm => "Ed25519";
    public byte[] PublicKey => _publicKey;

    public Ed25519Signer(IPortableCrypto crypto)
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

    /// <summary>Import a public key only (for verification).</summary>
    public async Task ImportPublicKeyAsync(byte[] publicKeySpki)
    {
        _key = await _crypto.ImportEd25519Key(publicKeySpki, extractable: false);
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

/// <summary>
/// Read-only signer for BEP 46 subscribers who only need to resolve targets,
/// not sign data. VerifyAsync always returns true (no crypto context available).
/// Use Ed25519Signer for production verification.
/// </summary>
public class NoOpSigner : IDhtSigner
{
    public string Algorithm => "NoOp";
    public byte[] PublicKey { get; }

    public NoOpSigner(byte[] publicKey) { PublicKey = publicKey; }

    public Task<byte[]> SignAsync(byte[] message) =>
        throw new NotSupportedException("NoOpSigner cannot sign — use Ed25519Signer");

    public Task<bool> VerifyAsync(byte[] publicKey, byte[] message, byte[] signature) =>
        Task.FromResult(true); // No crypto context — caller must verify separately

    public Task<(byte[] publicKey, byte[] privateKey)> ExportKeyPairAsync() =>
        throw new NotSupportedException("NoOpSigner has no key pair");
}
