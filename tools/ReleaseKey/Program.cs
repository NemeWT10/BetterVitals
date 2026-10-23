using System.Security.Cryptography;
using BetterVitals.Platform.Updates;

// Release signing key tool. The private key file must never be committed or shared.
if (args.Length >= 2 && args[0] == "new")
{
    var keyPath = Path.GetFullPath(args[1]);
    if (File.Exists(keyPath))
    {
        Console.Error.WriteLine($"refusing to overwrite {keyPath}");
        return 1;
    }
    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    File.WriteAllBytes(keyPath, ecdsa.ExportPkcs8PrivateKey());
    Console.WriteLine("private key: " + keyPath);
    Console.WriteLine("public key (put into src/BetterVitals.Platform/Updates/UpdateKey.cs):");
    Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    return 0;
}

if (args.Length >= 3 && args[0] == "sign")
{
    var key = File.ReadAllBytes(args[1]);
    var exe = Path.GetFullPath(args[2]);
    byte[] hash;
    using (var f = File.OpenRead(exe)) hash = SHA256.HashData(f);
    var sig = ReleaseSignature.Sign(key, hash);
    var name = Path.GetFileName(exe);
    File.WriteAllText(exe + ".sha256", $"{Convert.ToHexString(hash)}  {name}\n");
    File.WriteAllText(exe + ".sig", Convert.ToBase64String(sig) + "\n");
    Console.WriteLine($"sha256 {Convert.ToHexString(hash)}");
    Console.WriteLine($"signed {name}");
    return 0;
}

if (args.Length >= 2 && args[0] == "public")
{
    using var ecdsa = ECDsa.Create();
    ecdsa.ImportPkcs8PrivateKey(File.ReadAllBytes(args[1]), out _);
    Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    return 0;
}

Console.Error.WriteLine("usage: ReleaseKey new <keyfile> | sign <keyfile> <exe> | public <keyfile>");
return 2;
