using System.Text.Json;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Support;

/// <summary>
/// Shared scaffolding for the Kimi Web tests: a valid bundled Kimi install
/// (portable node + pinned lockfile + manifest + entry), a real Node 22
/// toolchain copy, and a real Node script standing in for <c>kimi web</c>.
/// The fake server binds loopback, prints the banner line with the
/// <c>#token=</c> fragment, serves the healthz / meta / shutdown routes and
/// the session export endpoint, and records the shutdown Authorization header
/// next to process.cwd() (the supervisor's neutral working directory).
/// </summary>
internal static class KimiWebTestFixture
{
    public const string TestToken = "test-token-123456";
    public const string KimiVersion = "0.29.1";

    public enum FakeServerMode
    {
        Full,
        NoShutdown,
        ExitAfterReady,
        MetaRejects
    }

    public static string KimiPackageDirectory(string installDirectory) => Path.Combine(
        installDirectory, "tools", "kimi", "node_modules", "@moonshot-ai", "kimi-code");

    public static string EntryPath(string installDirectory) =>
        Path.Combine(KimiPackageDirectory(installDirectory), "dist", "main.mjs");

    public static void InstallBundle(string installDirectory)
    {
        WriteFile(Path.Combine(installDirectory, "tools", "node", "node.exe"), "fake node");
        WriteFile(Path.Combine(installDirectory, "tools", "kimi", "package-lock.json"), "{}");
        var packageDir = KimiPackageDirectory(installDirectory);
        WriteFile(
            Path.Combine(packageDir, "package.json"),
            JsonSerializer.Serialize(new
            {
                name = "@moonshot-ai/kimi-code",
                version = KimiVersion,
                bin = new { kimi = "dist/main.mjs" }
            }));
        WriteFile(EntryPath(installDirectory), "// fake kimi web entry");
    }

    public static void SeedRealNode(RuntimePaths paths)
    {
        var nodePath = Path.Combine(
            TestWorkspace.RepositoryRoot, "TestResults", "node22", "node-v22.23.1-win-x64", "node.exe");
        if (!File.Exists(nodePath))
            Assert.Inconclusive("Node 22 toolchain is not available under TestResults/node22.");

        var nodeDirectory = Path.Combine(paths.InstallDirectory, "tools", "node");
        Directory.CreateDirectory(nodeDirectory);
        File.Copy(nodePath, Path.Combine(nodeDirectory, "node.exe"), overwrite: true);
    }

    public static void WriteFakeKimiWebEntry(string entryPath, FakeServerMode mode)
    {
        var shutdownBehavior = mode switch
        {
            FakeServerMode.NoShutdown => "res.writeHead(404); res.end(); return;",
            _ => """
                fs.appendFileSync(SHUTDOWN_LOG, (req.headers.authorization || '') + '\n');
                res.writeHead(200); res.end('bye');
                server.close();
                setTimeout(() => process.exit(0), 50);
                return;
                """
        };
        var exitAfterReady = mode == FakeServerMode.ExitAfterReady
            ? "setTimeout(() => { server.close(); process.exit(0); }, 2000);"
            : "";
        var metaAuth = mode == FakeServerMode.MetaRejects
            ? "res.writeHead(401); res.end(); return;"
            : """
              const auth = req.headers.authorization || '';
              if (auth !== 'Bearer ' + TOKEN) { res.writeHead(401); res.end(); return; }
              res.writeHead(200, { 'content-type': 'application/json' });
              res.end('{"server_version":"0.29.1-test"}');
              return;
              """;

        var script = $$"""
            import http from 'node:http';
            import fs from 'node:fs';
            import path from 'node:path';

            const TOKEN = '{{TestToken}}';
            const SHUTDOWN_LOG = path.join(process.cwd(), 'shutdown-headers.log');

            const server = http.createServer((req, res) => {
              const url = new URL(req.url, 'http://127.0.0.1');
              if (url.pathname === '/api/v1/healthz') {
                res.writeHead(200, { 'content-type': 'application/json' });
                res.end('{"ok":true}');
                return;
              }
              if (url.pathname === '/api/v1/meta') {
                {{metaAuth}}
              }
              if (url.pathname === '/api/v1/shutdown') {
                {{shutdownBehavior}}
              }
              const exportMatch = url.pathname.match(/^\/api\/v1\/sessions\/([^/]+)\/export$/);
              if (exportMatch) {
                const id = exportMatch[1];
                if (id === 'redirect') {
                  res.writeHead(302, { location: 'http://evil.example/steal.zip' });
                  res.end();
                  return;
                }
                if (id === 'nonzip') {
                  res.writeHead(200, { 'content-type': 'application/zip' });
                  res.end('not-a-zip-body');
                  return;
                }
                res.writeHead(200, { 'content-type': 'application/zip' });
                res.end(Buffer.from([0x50, 0x4b, 0x03, 0x04, 1, 2, 3]));
                return;
              }
              res.writeHead(404);
              res.end();
            });

            server.listen(0, '127.0.0.1', () => {
              const address = server.address();
              // Deliberately place the token on stderr as an adversarial
              // diagnostic fixture. The supervisor must drain this stream
              // without persisting provider-controlled output.
              console.error(`sensitive diagnostic token=${TOKEN}`);
              console.log(`  Local:    http://127.0.0.1:${address.port}/#token=${TOKEN}`);
              {{exitAfterReady}}
            });
            """;
        WriteFile(entryPath, script);
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
