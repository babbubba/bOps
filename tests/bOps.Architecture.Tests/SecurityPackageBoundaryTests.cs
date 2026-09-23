// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

/// <summary>V1.3-J: production TLS/certificate diagnostics must never expose private keys or read arbitrary files.</summary>
public sealed class SecurityPackageBoundaryTests
{
    [Fact]
    public void SecurityProductionSource_ContainsNoPrivateKeyExportOrCertificateFileReader()
    {
        var root = FindRoot();
        var source = Directory.EnumerateFiles(Path.Combine(root, "src", "packages", "bOps.Packages.Security"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        var forbidden = new[] { "Export(X509ContentType.Pfx)", "ExportPkcs8PrivateKey", "ExportEncryptedPkcs8PrivateKey", "ExportRSAPrivateKey", "ExportECPrivateKey", "ExportSubjectPublicKeyInfo", "ReadAllBytes", "File.Open", "certificate.pem" };
        Assert.DoesNotContain(forbidden, term => source.Any(text => text.Contains(term, StringComparison.Ordinal)));
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
