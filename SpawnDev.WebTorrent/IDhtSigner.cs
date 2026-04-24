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
        var spki = await _crypto.ExportPublicKeySpki(_key);
        // BEP 44 requires raw 32-byte Ed25519 key, not 44-byte SPKI (strip 12-byte DER prefix)
        _publicKey = spki.Length == 44 ? spki[12..] : spki;
    }

    /// <summary>Import an existing Ed25519 key pair.</summary>
    public async Task ImportKeyAsync(byte[] publicKeySpki, byte[] privateKeyPkcs8)
    {
        _key = await _crypto.ImportEd25519Key(publicKeySpki, privateKeyPkcs8, extractable: true);
        // Strip SPKI prefix if present
        _publicKey = publicKeySpki.Length == 44 ? publicKeySpki[12..] : publicKeySpki;
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
            // BEP 44 transmits the 32-byte raw Ed25519 pubkey on the wire; BlazorJS.Cryptography's
            // ImportEd25519Key expects 44-byte SPKI. Wrap raw keys in the SPKI prefix so verify
            // works for both formats transparently.
            var spki = publicKey.Length == 32 ? BuildSpkiFromRaw(publicKey) : publicKey;
            var peerKey = await _crypto.ImportEd25519Key(spki, extractable: false);
            return await _crypto.Verify(peerKey, message, signature);
        }
        catch
        {
            return false;
        }
    }

    // Ed25519 SPKI prefix (12 bytes): SEQUENCE + AlgorithmIdentifier(id-Ed25519 = 1.3.101.112) + BIT STRING header.
    // Same constant used by the SPKI decoder side in BlazorJS.Cryptography.
    private static byte[] BuildSpkiFromRaw(byte[] raw32)
    {
        var prefix = new byte[] { 0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00 };
        var spki = new byte[prefix.Length + 32];
        Buffer.BlockCopy(prefix, 0, spki, 0, prefix.Length);
        Buffer.BlockCopy(raw32, 0, spki, prefix.Length, 32);
        return spki;
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
/// Read-only Ed25519 verifier for BEP 46 subscribers who only need to resolve targets and
/// verify signatures, not sign data. Uses Ed25519Signer.ImportPublicKeyAsync internally.
/// Works on both desktop and browser via SpawnDev.BlazorJS.Cryptography.
/// </summary>
public class ReadOnlyEd25519Verifier : IDhtSigner
{
    private readonly Ed25519Signer _inner;

    public string Algorithm => "Ed25519-ReadOnly";
    public byte[] PublicKey { get; }

    private ReadOnlyEd25519Verifier(Ed25519Signer inner, byte[] publicKey)
    {
        _inner = inner;
        PublicKey = publicKey;
    }

    /// <summary>Create a read-only verifier from a raw 32-byte public key.</summary>
    public static async Task<ReadOnlyEd25519Verifier> CreateAsync(IPortableCrypto crypto, byte[] publicKey)
    {
        var signer = new Ed25519Signer(crypto);
        // Import as SPKI if raw 32-byte key, otherwise pass through
        var spki = publicKey.Length == 32 ? BuildSpkiFromRaw(publicKey) : publicKey;
        await signer.ImportPublicKeyAsync(spki);
        return new ReadOnlyEd25519Verifier(signer, publicKey);
    }

    public Task<byte[]> SignAsync(byte[] message) =>
        throw new NotSupportedException("ReadOnlyEd25519Verifier cannot sign - use Ed25519Signer");

    public Task<bool> VerifyAsync(byte[] publicKey, byte[] message, byte[] signature) =>
        _inner.VerifyAsync(publicKey, message, signature);

    public Task<(byte[] publicKey, byte[] privateKey)> ExportKeyPairAsync() =>
        throw new NotSupportedException("ReadOnlyEd25519Verifier has no private key");

    /// <summary>Build 44-byte SPKI from 32-byte raw Ed25519 public key.</summary>
    private static byte[] BuildSpkiFromRaw(byte[] rawKey)
    {
        // DER SPKI prefix for Ed25519: 30 2A 30 05 06 03 2B 65 70 03 21 00
        var spki = new byte[44];
        new byte[] { 0x30, 0x2A, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x03, 0x21, 0x00 }
            .CopyTo(spki, 0);
        rawKey.CopyTo(spki, 12);
        return spki;
    }
}

/// <summary>
/// No-op signer for testing only. VerifyAsync always returns true.
/// DO NOT use in production - accepts forged values without verification.
/// </summary>
public class NoOpSigner : IDhtSigner
{
    public string Algorithm => "NoOp";
    public byte[] PublicKey { get; }

    public NoOpSigner(byte[] publicKey) { PublicKey = publicKey; }

    public Task<byte[]> SignAsync(byte[] message) =>
        throw new NotSupportedException("NoOpSigner cannot sign - use Ed25519Signer");

    public Task<bool> VerifyAsync(byte[] publicKey, byte[] message, byte[] signature) =>
        Task.FromResult(true); // Test only - no verification

    public Task<(byte[] publicKey, byte[] privateKey)> ExportKeyPairAsync() =>
        throw new NotSupportedException("NoOpSigner has no key pair");
}
