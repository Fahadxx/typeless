using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Asn1;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;

namespace Typeless
{
    // Signs the unsigned release package on this PC with a new certificate whose key exists only in memory,
    // and trusts that certificate (elevated), so Windows 11 shows the top-level menu without a public certificate.
    public static class LocalSign
    {
        public const string Subject = "CN=Typeless Local";
        public const string LocalMsixName = "Typeless.local.msix";
        private const string MsixName = "Typeless.msix";
        private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
        private const string Sha256RsaOid = "1.2.840.113549.1.1.11";
        private const string ManifestNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        private const int ErrorCancelled = 1223;

        private static string AppDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // --sign-local: <app>\Typeless.msix (left untouched) to <app>\Typeless.local.msix, prints cert= and thumbprint=.
        public static int SignLocal()
        {
            string msix = Path.Combine(AppDir, MsixName);
            string? error = CheckSignable(msix);
            if (error != null)
            {
                Log.Write("Sign local: " + error);
                Console.Error.WriteLine(error);
                return 1;
            }

            try
            {
                (string cert, string thumbprint) = SignCopy(msix, Path.Combine(AppDir, LocalMsixName));
                Console.WriteLine("cert=" + cert);
                Console.WriteLine("thumbprint=" + thumbprint);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("Sign local failed: " + ex.Message);
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // --register local: sign, trust (UAC), then the normal register. Any failure falls back to the classic menu.
        public static int RegisterLocal()
        {
            string msix = Path.Combine(AppDir, MsixName);
            string local = Path.Combine(AppDir, LocalMsixName);
            string? error = CheckSignable(msix);
            if (error != null)
            {
                Log.Write("Register local: " + error + ", normal register.");
                return Setup.Register();
            }

            string cert;
            try
            {
                cert = SignCopy(msix, local).Cert;
            }
            catch (Exception ex)
            {
                Log.Write("Register local: signing failed: " + ex.Message);
                TryDelete(local);
                return Setup.Register();
            }

            int? rc = RunElevated("--trust-cert " + cert);
            if (rc != 0)
            {
                TryDelete(local);
                Log.Write(rc == null ? "Register local: trust declined" : $"Register local: trust failed rc={rc}");
            }
            else
            {
                Log.Write("Register local: certificate trusted.");
            }
            return Setup.Register();
        }

        // --trust-cert <base64 DER>, elevated. Uses only its argument: under UAC it may run as another user.
        // rc 0 trusted, 1 invalid certificate, 5 not elevated.
        public static int TrustCert(string base64)
        {
            byte[] der;
            try
            {
                der = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                Console.Error.WriteLine("Invalid certificate: not base64");
                return 1;
            }

            X509Certificate2 cert;
            try
            {
                if (X509Certificate2.GetCertContentType(der) != X509ContentType.Cert)
                {
                    Console.Error.WriteLine("Invalid certificate: not a DER certificate");
                    return 1;
                }
                cert = new X509Certificate2(der);
            }
            catch (CryptographicException)
            {
                Console.Error.WriteLine("Invalid certificate: cannot be read");
                return 1;
            }

            using (cert)
            {
                string? error = Validate(cert, der);
                if (error != null)
                {
                    Console.Error.WriteLine("Invalid certificate: " + error);
                    return 1;
                }

                try
                {
                    using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadWrite);
                    RemoveOthers(store, cert.Thumbprint);
                    store.Add(cert);
                }
                catch (Exception ex) when (ex is CryptographicException || ex is UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("Administrator approval is needed");
                    return 5;
                }

                Log.Write("Trust cert: trusted " + cert.Thumbprint);
                Console.WriteLine("thumbprint=" + cert.Thumbprint);
                return 0;
            }
        }

        // --untrust-cert, elevated: removes every CN=Typeless Local certificate from LocalMachine\TrustedPeople.
        public static int UntrustCert()
        {
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                int removed = RemoveOthers(store, null);
                Log.Write($"Untrust cert: removed {removed}");
                Console.WriteLine($"removed={removed}");
                return 0;
            }
            catch (Exception ex) when (ex is CryptographicException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Administrator approval is needed");
                return 5;
            }
        }

        // --local-cert [thumbprint]: trusted=yes|no (any CN=Typeless Local, or that thumbprint), then thumbprint= per one in the store.
        public static int LocalCert(string? thumbprint)
        {
            string? want = thumbprint?.Replace(" ", "").Trim();
            var found = new List<string>();
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (X509Certificate2 c in store.Certificates)
                {
                    using (c)
                    {
                        if (string.Equals(c.Subject, Subject, StringComparison.Ordinal)) found.Add(c.Thumbprint);
                    }
                }
            }
            catch (CryptographicException ex)
            {
                Log.Write("Local cert: store not readable: " + ex.Message);
            }

            bool trusted = want == null
                ? found.Count > 0
                : found.Exists(t => string.Equals(t, want, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("trusted=" + (trusted ? "yes" : "no"));
            foreach (string t in found) Console.WriteLine("thumbprint=" + t);
            return 0;
        }

        // Null when the package is ours to sign: present, unsigned, publisher CN=Typeless Local.
        private static string? CheckSignable(string msix)
        {
            if (!File.Exists(msix)) return "No Typeless.msix";
            try
            {
                using ZipArchive zip = ZipFile.OpenRead(msix);
                if (zip.GetEntry("AppxSignature.p7x") != null) return "Package is already signed";
                ZipArchiveEntry? manifest = zip.GetEntry("AppxManifest.xml");
                string? publisher = null;
                if (manifest != null)
                {
                    using Stream s = manifest.Open();
                    using XmlReader reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                    publisher = XDocument.Load(reader).Root?.Element(XName.Get("Identity", ManifestNs))?.Attribute("Publisher")?.Value;
                }
                if (!string.Equals(publisher, Subject, StringComparison.Ordinal)) return "Package publisher is not " + Subject;
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is XmlException || ex is UnauthorizedAccessException)
            {
                return "Package cannot be read: " + ex.Message;
            }
        }

        // Signs a copy under a temp name, then moves it over the target. The key is never persisted or exported.
        // The temp name keeps the .msix extension, which the signing layer uses to pick the Appx SIP.
        private static (string Cert, string Thumbprint) SignCopy(string msix, string target)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(target)!, "Typeless.local.tmp.msix");
            try
            {
                File.Copy(msix, tmp, overwrite: true);
                using RSA rsa = RSA.Create(3072);
                var req = new CertificateRequest(Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(CodeSigningOid) }, false));
                req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

                int hr = SignFile(tmp, cert);
                if (hr != 0) throw new InvalidOperationException($"Signing failed (0x{hr:X8})");
                string b64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));
                File.Move(tmp, target, overwrite: true);

                Log.Write("Sign local: signed " + Path.GetFileName(target) + ", thumbprint " + cert.Thumbprint);
                return (b64, cert.Thumbprint);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        // Exactly what --trust-cert accepts; null when valid.
        private static string? Validate(X509Certificate2 cert, byte[] der)
        {
            if (!string.Equals(cert.Subject, Subject, StringComparison.Ordinal)) return "subject is not " + Subject;
            if (!string.Equals(cert.Issuer, Subject, StringComparison.Ordinal)) return "issuer is not " + Subject;
            if (cert.HasPrivateKey) return "has a private key";

            X509Extension? bc = cert.Extensions["2.5.29.19"];
            if (bc == null || new X509BasicConstraintsExtension(bc, bc.Critical).CertificateAuthority) return "not CA=false";

            X509Extension? eku = cert.Extensions["2.5.29.37"];
            if (eku == null) return "no enhanced key usage";
            OidCollection usages = new X509EnhancedKeyUsageExtension(eku, eku.Critical).EnhancedKeyUsages;
            if (usages.Count != 1 || usages[0].Value != CodeSigningOid) return "enhanced key usage is not code signing only";

            using RSA? key = cert.GetRSAPublicKey();
            if (key == null || key.KeySize < 2048) return "not RSA 2048 or more";

            DateTime now = DateTime.Now;
            if (now < cert.NotBefore || now > cert.NotAfter) return "not valid now";

            if (!SelfSigned(der, key)) return "not self-signed";
            return null;
        }

        // Verifies the certificate's signature over its TBS bytes with its own public key (sha256RSA only).
        private static bool SelfSigned(byte[] der, RSA key)
        {
            try
            {
                var outer = new AsnReader(der, AsnEncodingRules.DER);
                AsnReader seq = outer.ReadSequence();
                outer.ThrowIfNotEmpty();
                ReadOnlyMemory<byte> tbs = seq.ReadEncodedValue();
                AsnReader alg = seq.ReadSequence();
                string oid = alg.ReadObjectIdentifier();
                byte[] signature = seq.ReadBitString(out int unusedBits);
                seq.ThrowIfNotEmpty();
                if (oid != Sha256RsaOid || unusedBits != 0) return false;
                return key.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex) when (ex is AsnContentException || ex is CryptographicException)
            {
                return false;
            }
        }

        // Removes every CN=Typeless Local certificate except keepThumbprint; returns how many went.
        private static int RemoveOthers(X509Store store, string? keepThumbprint)
        {
            int removed = 0;
            foreach (X509Certificate2 c in store.Certificates)
            {
                using (c)
                {
                    if (!string.Equals(c.Subject, Subject, StringComparison.Ordinal)) continue;
                    if (keepThumbprint != null && string.Equals(c.Thumbprint, keepThumbprint, StringComparison.OrdinalIgnoreCase)) continue;
                    store.Remove(c);
                    removed++;
                }
            }
            return removed;
        }

        // Runs this exe elevated (UAC) and waits. Null when declined (or TYPELESS_NO_ELEVATE=1), else the exit code.
        private static int? RunElevated(string arguments)
        {
            if (Environment.GetEnvironmentVariable("TYPELESS_NO_ELEVATE") == "1") return null;
            string? exe = Environment.ProcessPath;
            if (exe == null) return -1;
            try
            {
                using Process? p = Process.Start(new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p == null) return -1;
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("Elevation failed: " + ex.Message);
                return -1;
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        // SignerSignEx2 with APPX_SIP_CLIENT_DATA, SHA-256, SIGNER_CERT_POLICY_CHAIN_NO_ROOT, no timestamp.
        // Every struct lives in its own HGlobal block, freed afterwards.
        private static int SignFile(string path, X509Certificate2 cert)
        {
            var blocks = new List<IntPtr>();
            IntPtr Alloc<T>(T value) where T : struct
            {
                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
                blocks.Add(p);
                Marshal.StructureToPtr(value, p, false);
                return p;
            }

            IntPtr ctx = IntPtr.Zero;
            IntPtr sip = IntPtr.Zero;
            try
            {
                IntPtr name = Marshal.StringToHGlobalUni(path);
                blocks.Add(name);
                IntPtr index = Marshal.AllocHGlobal(sizeof(uint));
                blocks.Add(index);
                Marshal.WriteInt32(index, 0);
                IntPtr file = Alloc(new SIGNER_FILE_INFO { cbSize = (uint)Marshal.SizeOf<SIGNER_FILE_INFO>(), pwszFileName = name });
                IntPtr subj = Alloc(new SIGNER_SUBJECT_INFO { cbSize = (uint)Marshal.SizeOf<SIGNER_SUBJECT_INFO>(), pdwIndex = index, dwSubjectChoice = 1, pSignerFileInfo = file });
                IntPtr store = Alloc(new SIGNER_CERT_STORE_INFO { cbSize = (uint)Marshal.SizeOf<SIGNER_CERT_STORE_INFO>(), pSigningCert = cert.Handle, dwCertPolicy = 8 });
                IntPtr sc = Alloc(new SIGNER_CERT { cbSize = (uint)Marshal.SizeOf<SIGNER_CERT>(), dwCertChoice = 2, pCertStoreInfo = store });
                IntPtr sig = Alloc(new SIGNER_SIGNATURE_INFO { cbSize = (uint)Marshal.SizeOf<SIGNER_SIGNATURE_INFO>(), algidHash = 0x800c, dwAttrChoice = 0 });
                IntPtr prm = Alloc(new SIGNER_SIGN_EX2_PARAMS { pSubjectInfo = subj, pSigningCert = sc, pSignatureInfo = sig });
                sip = Alloc(new APPX_SIP_CLIENT_DATA { pSignerParams = prm });

                int hr = SignerSignEx2(0, subj, sc, sig, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sip,
                    out ctx, IntPtr.Zero, IntPtr.Zero);
                GC.KeepAlive(cert);
                return hr;
            }
            finally
            {
                if (sip != IntPtr.Zero)
                {
                    IntPtr state = Marshal.PtrToStructure<APPX_SIP_CLIENT_DATA>(sip).pAppxSipState;
                    if (state != IntPtr.Zero) Marshal.Release(state);
                }
                if (ctx != IntPtr.Zero) SignerFreeSignerContext(ctx);
                foreach (IntPtr p in blocks) Marshal.FreeHGlobal(p);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct SIGNER_FILE_INFO { public uint cbSize; public IntPtr pwszFileName; public IntPtr hFile; }
        [StructLayout(LayoutKind.Sequential)] private struct SIGNER_SUBJECT_INFO { public uint cbSize; public IntPtr pdwIndex; public uint dwSubjectChoice; public IntPtr pSignerFileInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct SIGNER_CERT_STORE_INFO { public uint cbSize; public IntPtr pSigningCert; public uint dwCertPolicy; public IntPtr hCertStore; }
        [StructLayout(LayoutKind.Sequential)] private struct SIGNER_CERT { public uint cbSize; public uint dwCertChoice; public IntPtr pCertStoreInfo; public IntPtr hwnd; }
        [StructLayout(LayoutKind.Sequential)] private struct SIGNER_SIGNATURE_INFO { public uint cbSize; public uint algidHash; public uint dwAttrChoice; public IntPtr pAttrAuthcode; public IntPtr psAuthenticated; public IntPtr psUnauthenticated; }
        [StructLayout(LayoutKind.Sequential)] private struct SIGNER_SIGN_EX2_PARAMS { public uint dwFlags; public IntPtr pSubjectInfo; public IntPtr pSigningCert; public IntPtr pSignatureInfo; public IntPtr pProviderInfo; public uint dwTimestampFlags; public IntPtr pszAlgorithmOid; public IntPtr pwszTimestampURL; public IntPtr pCryptAttrs; public IntPtr pSipData; public IntPtr pSignerContext; public IntPtr pCryptoPolicy; public IntPtr pReserved; }
        [StructLayout(LayoutKind.Sequential)] private struct APPX_SIP_CLIENT_DATA { public IntPtr pSignerParams; public IntPtr pAppxSipState; }

        [DllImport("mssign32.dll", ExactSpelling = true)]
        private static extern int SignerSignEx2(uint dwFlags, IntPtr pSubjectInfo, IntPtr pSignerCert, IntPtr pSignatureInfo,
            IntPtr pProviderInfo, uint dwTimestampFlags, IntPtr pszAlgorithmOid, IntPtr pwszHttpTimeStamp, IntPtr psRequest, IntPtr pSipData,
            out IntPtr ppSignerContext, IntPtr pCryptoPolicy, IntPtr pReserved);

        [DllImport("mssign32.dll", ExactSpelling = true)]
        private static extern int SignerFreeSignerContext(IntPtr pSignerContext);
    }
}
