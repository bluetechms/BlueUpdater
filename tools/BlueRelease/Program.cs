using BlueRelease;

// BlueRelease keygen  --name primary --out <folder outside any repo>
// BlueRelease pack    --product printing-outlet --version 1.1.0.0 --src <publish folder> --exe BluePOSPrinting.exe
//                     --key <private.pk8.b64> --out <folder> [--expires-days 400] [--min-updater 1.0.0.0] [--exclude a/*.x]...
// BlueRelease verify  --dir <folder> --pubkey <public.b64> --product printing-outlet
// BlueRelease upload  --api https://api.bluehms.com/api --dir <folder> --product printing-outlet [--canary 12,34] [--notes "..."]
// BlueRelease state   --api <url> --id <release id> [--active true|false] [--blocked true|false] [--canary "ids" | --canary ""]
// BlueRelease list    --api <url> [--what releases|instances]
//
// Secrets come from the environment, never the command line (it ends up in shell history):
//   BLUERELEASE_PASSPHRASE  passphrase of the signing key
//   BLUERELEASE_TOKEN       a super-admin session token (the `auth` header value)

try
{
    if (args.Length == 0) return Usage();
    var cmd = args[0];
    string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    List<string> Opts(string name) { var l = new List<string>(); for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) l.Add(args[i + 1]); return l; }
    string Req(string name) => Opt(name) ?? throw new ArgumentException($"missing {name}");
    string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new ArgumentException($"set the {name} environment variable");

    switch (cmd)
    {
        case "keygen":
        {
            var (priv, pub, b64) = ReleaseTool.KeyGen(Req("--out"), Req("--name"), Env("BLUERELEASE_PASSPHRASE"));
            Console.WriteLine($"Private key (encrypted): {priv}");
            Console.WriteLine($"Public key:              {pub}");
            Console.WriteLine();
            Console.WriteLine("Add this line to BlueUpdater/src/BlueUpdater/pubkeys.txt (rebuild the updater) and to the");
            Console.WriteLine("CLIENT_UPDATE_PUBKEYS environment variable in Dokploy (comma-separated if there are several):");
            Console.WriteLine(b64);
            Console.WriteLine();
            Console.WriteLine("Keep the private file OUT of every git repo. Store a copy of it, and the passphrase, in two separate safe places.");
            return 0;
        }
        case "pack":
        {
            using var key = ReleaseTool.LoadPrivateKey(Req("--key"), Env("BLUERELEASE_PASSPHRASE"));
            var r = ReleaseTool.Pack(Req("--product"), Req("--version"), Req("--src"), Req("--exe"), key, Req("--out"),
                int.TryParse(Opt("--expires-days"), out var days) ? days : 400, Opt("--min-updater"), Opts("--exclude"));
            Console.WriteLine($"Packaged {r.Manifest.Files.Count} files, {r.Manifest.Package!.Size:N0} bytes, sha256 {r.Manifest.Package.Sha256}");
            Console.WriteLine($"Expires {r.Manifest.ExpiresAt:u}. Output in {Req("--out")}");
            return 0;
        }
        case "verify":
            ReleaseTool.Verify(Req("--dir"), File.ReadAllText(Req("--pubkey")).Trim(), Req("--product"));
            Console.WriteLine("OK: signature, hashes and file rules all pass.");
            return 0;
        case "upload":
            Console.WriteLine(await ReleaseTool.UploadAsync(Req("--api"), Env("BLUERELEASE_TOKEN"), Req("--dir"), Req("--product"), Opt("--canary"), Opt("--notes")));
            Console.WriteLine("Uploaded INACTIVE. Activate with: BlueRelease state --id <id> --active true --canary \"<company ids>\"");
            return 0;
        case "state":
        {
            bool? B(string n) => Opt(n) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : null;
            Console.WriteLine(await ReleaseTool.SetStateAsync(Req("--api"), Env("BLUERELEASE_TOKEN"), int.Parse(Req("--id")), B("--active"), B("--blocked"), Opt("--canary")));
            return 0;
        }
        case "list":
            Console.WriteLine(await ReleaseTool.ListAsync(Req("--api"), Env("BLUERELEASE_TOKEN"), Opt("--what") ?? "releases"));
            return 0;
        default:
            return Usage();
    }
}
catch (Exception e)
{
    Console.Error.WriteLine("ERROR: " + e.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Commands: keygen | pack | verify | upload | state | list   (see the header of Program.cs)");
    return 2;
}
