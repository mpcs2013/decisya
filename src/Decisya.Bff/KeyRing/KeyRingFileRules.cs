using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;

namespace Decisya.Bff.KeyRing;

/// <summary>Why a top-level key file fails the strict rule (G3 G4-122-02, rule 2). A closed list: it reaches the log.</summary>
internal enum KeyFileVerdict
{
    Ok,
    Unreadable,
    NotRegularFile,
    PlaintextMaterial,
    NotCertificateWrapped,
}

/// <summary>What the check learned from one file. Never key material.</summary>
internal sealed record KeyFileInfo(
    KeyFileVerdict Verdict, string? KeyId, DateTimeOffset? Expiration, IReadOnlyList<string> WrappingFingerprints);

/// <summary>
/// The strict plaintext rule (#122, G3 G4-122-02). A file passes only if all of these hold: no
/// <c>masterKey</c> or <c>unencryptedKey</c> element at any depth; exactly one <c>encryptedSecret</c>;
/// its <c>decryptorType</c>, up to the first comma, is the framework's certificate decryptor; and it holds
/// an <c>EncryptedData</c> element. "Has an <c>encryptedSecret</c>" is not enough: a key written through
/// <c>NullXmlEncryptor</c> has one and still carries the master key in clear. A <c>revocation</c> file
/// holds no secret and passes when it has none of the secret elements.
/// </summary>
internal static class KeyRingFileRules
{
    internal const string CertificateDecryptorType =
        "Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor";

    private const long MaxKeyFileBytes = 256 * 1024;

    internal static KeyFileInfo Inspect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var info = new FileInfo(path);
        if (info.LinkTarget is not null || !info.Exists || info.Length > MaxKeyFileBytes)
        {
            return Fail(KeyFileVerdict.NotRegularFile);
        }

        XDocument document;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(
                stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            return Fail(KeyFileVerdict.Unreadable);
        }

        return Inspect(document);
    }

    internal static KeyFileInfo Inspect(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.Root;
        if (root is null)
        {
            return Fail(KeyFileVerdict.Unreadable);
        }

        var keyId = root.Attribute("id")?.Value;
        var expiration = DateTimeOffset.TryParse(
            root.Elements().FirstOrDefault(e => Is(e, "expirationDate"))?.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed) ? parsed : (DateTimeOffset?)null;

        if (document.Descendants().Any(e => Is(e, "masterKey") || Is(e, "unencryptedKey")))
        {
            return new KeyFileInfo(KeyFileVerdict.PlaintextMaterial, keyId, expiration, []);
        }

        var secrets = document.Descendants().Where(e => Is(e, "encryptedSecret")).ToList();
        if (Is(root, "revocation") && secrets.Count == 0)
        {
            return new KeyFileInfo(KeyFileVerdict.Ok, keyId, expiration, []);
        }

        if (!Is(root, "key") || secrets.Count != 1)
        {
            return new KeyFileInfo(KeyFileVerdict.NotCertificateWrapped, keyId, expiration, []);
        }

        var secret = secrets[0];
        var decryptorType = secret.Attribute("decryptorType")?.Value ?? string.Empty;
        var typeName = decryptorType.Split(',', 2)[0].Trim();
        if (!string.Equals(typeName, CertificateDecryptorType, StringComparison.Ordinal)
            || !secret.Descendants().Any(e => Is(e, "EncryptedData")))
        {
            return new KeyFileInfo(KeyFileVerdict.NotCertificateWrapped, keyId, expiration, []);
        }

        return new KeyFileInfo(KeyFileVerdict.Ok, keyId, expiration, Fingerprints(secret));
    }

    /// <summary>The SHA-256 fingerprints of the public certificates the wrapped key names (read-only, best effort).</summary>
    private static List<string> Fingerprints(XElement secret)
    {
        var result = new List<string>();
        foreach (var element in secret.Descendants().Where(e => Is(e, "X509Certificate")))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(element.Value.Trim()));
                result.Add(certificate.GetCertHashString(HashAlgorithmName.SHA256));
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                // Best effort: only the S-122-02 information event depends on this.
            }
        }

        return result;
    }

    private static bool Is(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static KeyFileInfo Fail(KeyFileVerdict verdict) => new(verdict, null, null, []);
}
