// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Microsoft.NET.Build.Containers.IntegrationTests;

[TestClass]
public class MultiArchArchiveIncrementalTests : SdkTest
{
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux | OperatingSystems.OSX)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Archive_cache_tracks_inner_destination_names(bool useMultithreading)
    {
        await using ContainerTestRegistry registry = new();
        var asset = TestAssetsManager.CopyTestAsset("HelloWorld", identifier: useMultithreading.ToString()).WithSource();
        string archive = Path.Combine(asset.TestRoot, "images.tar");
        string cache = Directory.CreateDirectory(Path.Combine(asset.TestRoot, "cache")).FullName;
        asset.WithProjectChanges(project => project.Root!.Add(
            new XElement("PropertyGroup", new XAttribute("Condition", "'$(RuntimeIdentifier)' == ''"),
                new XElement("ContainerArchiveOutputPath", archive)),
            new XElement("Target", new XAttribute("Name", "RecordContainerNames"),
                new XAttribute("AfterTargets", "_PublishSingleContainer"),
                new XElement("WriteLinesToFile", new XAttribute("File", "$(MSBuildProjectDirectory)/names-$(RuntimeIdentifier).txt"),
                    new XAttribute("Lines", "@(GeneratedContainerName)"), new XAttribute("Overwrite", "true")))));
        string[] arguments = [
            "publish", "-t:PublishContainer", "-v:normal", $"-mt:{useMultithreading}",
            "-p:EnableSdkContainerSupport=true", "-p:UseAppHost=false", "-p:SelfContained=false",
            "-p:RuntimeIdentifiers=\"linux-x64;linux-arm64\"",
            $"-p:ContainerBaseImage={registry.Address}/empty:latest", $"-p:ContainerRegistry={registry.Address}",
            "-p:ContainerRepository=names-test", "-p:ContainerImageTag=test"
        ];
        Publish([.. arguments, $"-p:ContainerArchiveOutputPath={archive}"]).Should().Pass();
        Assert.AreSequenceEqual(new[] { "names-test:test", "names-test:test" }, ReadNames());

        var changed = Publish(arguments);
        changed.Should().Pass();
        Assert.Contains("for runtime identifier", changed.StdOut!);
        string[] remoteNames = ReadNames();
        Assert.ContainsOnly(name => name != "names-test:test", remoteNames);
        var warm = Publish(arguments);
        warm.Should().Pass();
        Assert.DoesNotContain("for runtime identifier", warm.StdOut!);
        Assert.AreSequenceEqual(remoteNames, ReadNames());
        Publish([.. arguments, "-p:ContainerArchiveNoIncremental=true"]).Should().Pass();
        Assert.AreSequenceEqual(remoteNames, ReadNames());

        var local = Publish([.. arguments, $"-p:ContainerArchiveOutputPath={archive}"]);
        local.Should().Pass();
        Assert.Contains("for runtime identifier", local.StdOut!);
        string[] localNames = ReadNames();
        Assert.AreSequenceEqual(new[] { "names-test:test", "names-test:test" }, localNames);
        var warmLocal = Publish([.. arguments, $"-p:ContainerArchiveOutputPath={archive}"]);
        warmLocal.Should().Pass();
        Assert.DoesNotContain("for runtime identifier", warmLocal.StdOut!);
        Assert.AreSequenceEqual(localNames, ReadNames());
        Publish([.. arguments, $"-p:ContainerArchiveOutputPath={archive}", "-p:ContainerArchiveNoIncremental=true"]).Should().Pass();
        Assert.AreSequenceEqual(localNames, ReadNames());

        string[] ReadNames() => new[] { "linux-x64", "linux-arm64" }
            .Select(rid => File.ReadAllText(Path.Combine(asset.TestRoot, $"names-{rid}.txt")).Trim()).ToArray();

        Microsoft.DotNet.Cli.Utils.CommandResult Publish(string[] args)
            => new DotnetCommand(Log, args)
                .WithWorkingDirectory(asset.TestRoot)
                .WithEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "0")
                .WithEnvironmentVariable("DOTNET_CONTAINER_INSECURE_REGISTRIES", registry.Address)
                .WithEnvironmentVariable("SOURCE_DATE_EPOCH", "1700000000")
                .WithEnvironmentVariable("XDG_CACHE_HOME", cache)
                .WithEnvironmentVariable("HOME", cache)
                .Execute();
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux | OperatingSystems.OSX)]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task Default_publish_reuses_inner_images_and_outer_archive(bool useMultithreading, bool projectDefinedArchivePath, bool specialFilename)
    {
        await using ContainerTestRegistry registry = new();
        var asset = TestAssetsManager.CopyTestAsset("HelloWorld", identifier: $"{useMultithreading}-{projectDefinedArchivePath}-{specialFilename}").WithSource();
        string archive = Path.Combine(asset.TestRoot, specialFilename ? "images;=signed.tar.gz" : "images.tar.gz");
        string cache = Directory.CreateDirectory(Path.Combine(asset.TestRoot, "cache")).FullName;
        if (projectDefinedArchivePath)
        {
            asset.WithProjectChanges(project => project.Root!.Add(
                new XElement("PropertyGroup", new XAttribute("Condition", "'$(RuntimeIdentifier)' == ''"),
                    new XElement("ContainerArchiveOutputPath", archive))));
        }
        string[] arguments = [
            "publish", "-t:PublishContainer", "-v:normal", $"-mt:{useMultithreading}",
            "-p:EnableSdkContainerSupport=true", "-p:UseAppHost=false", "-p:SelfContained=false",
            "-p:RuntimeIdentifiers=\"linux-x64;linux-arm64\"",
            $"-p:ContainerBaseImage={registry.Address}/empty:latest",
            "-p:ContainerRepository=incremental-test", "-p:ContainerImageTag=test"
        ];
        if (!projectDefinedArchivePath) arguments = [.. arguments, $"-p:ContainerArchiveOutputPath={archive}"];
        var first = Publish(arguments);
        first.Should().Pass();
        Assert.Contains("for runtime identifier", first.StdOut!);
        Assert.IsTrue(File.Exists(archive));
        string[] initialContent = ReadArchiveContent();
        DateTime sentinel = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(archive, sentinel);

        // Deliberately do not set ContainerArchiveNoIncremental globally: its default
        // must reach the inner MSBuild calls through the publishing targets themselves.
        var second = Publish(arguments);
        second.Should().Pass();
        Assert.DoesNotContain("for runtime identifier", second.StdOut!);
        Assert.AreEqual(sentinel, File.GetLastWriteTimeUtc(archive));
        Assert.AreSequenceEqual(initialContent, ReadArchiveContent());
        Assert.AreEqual(2, registry.ConfigurationRequests);
        Assert.HasCount(3, Directory.GetFiles(Path.Combine(asset.TestRoot, "obj"), "*.cache.json", SearchOption.AllDirectories));

        // MSBuild boolean conditions are case-insensitive, for both inner and outer caches.
        var mixedCase = Publish([.. arguments, "-p:ContainerArchiveNoIncremental=False"]);
        mixedCase.Should().Pass();
        Assert.DoesNotContain("for runtime identifier", mixedCase.StdOut!);
        Assert.AreEqual(sentinel, File.GetLastWriteTimeUtc(archive));
        Assert.AreEqual(2, registry.ConfigurationRequests);

        // A mutable tag still resolves every platform manifest before checking the cache.
        registry.ChangeBaseManifests("changed");
        var changedBase = Publish(arguments);
        changedBase.Should().Pass();
        Assert.Contains("for runtime identifier", changedBase.StdOut!);
        Assert.AreNotEqual(sentinel, File.GetLastWriteTimeUtc(archive));
        Assert.AreEqual(4, registry.ConfigurationRequests);

        // Explicit opt-out must bypass both caches, with MSBuild's boolean casing.
        File.SetLastWriteTimeUtc(archive, sentinel);
        var uncached = Publish([.. arguments, "-p:ContainerArchiveNoIncremental=True"]);
        uncached.Should().Pass();
        Assert.Contains("for runtime identifier", uncached.StdOut!);
        Assert.AreNotEqual(sentinel, File.GetLastWriteTimeUtc(archive));

        // Corrupting the outer archive must rebuild it while retaining intact inner images.
        string[] beforeDamage = ReadArchiveContent();
        File.WriteAllText(archive, "damaged");
        var repaired = Publish(arguments);
        repaired.Should().Pass();
        Assert.DoesNotContain("for runtime identifier", repaired.StdOut!);
        Assert.IsGreaterThan(7L, new FileInfo(archive).Length);
        Assert.AreSequenceEqual(beforeDamage, ReadArchiveContent());

        string[] ReadArchiveContent()
        {
            Dictionary<string, byte[]> files = [];
            using (TarReader reader = new(File.OpenRead(archive)))
            {
                while (reader.GetNextEntry() is { } entry)
                {
                    if (entry.DataStream is not { } data) continue;
                    using MemoryStream bytes = new();
                    data.CopyTo(bytes);
                    files.Add(entry.Name, bytes.ToArray());
                }
            }
            foreach ((string name, byte[] bytes) in files)
            {
                if (name.StartsWith("blobs/sha256/", StringComparison.Ordinal))
                    Assert.AreEqual(name["blobs/sha256/".Length..], Convert.ToHexStringLower(SHA256.HashData(bytes)));
                JsonDocument document;
                try { document = JsonDocument.Parse(bytes); }
                catch (JsonException) { continue; }
                using (document)
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (string property in new[] { "manifests", "layers" })
                        if (document.RootElement.TryGetProperty(property, out JsonElement descriptors))
                            foreach (JsonElement descriptor in descriptors.EnumerateArray()) Validate(descriptor);
                    if (document.RootElement.TryGetProperty("config", out JsonElement config)
                        && config.TryGetProperty("digest", out _)) Validate(config);
                }
            }
            return files.Select(pair => pair.Key + ":" + Convert.ToHexStringLower(SHA256.HashData(pair.Value)))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();

            void Validate(JsonElement descriptor)
            {
                string path = "blobs/sha256/" + DigestUtils.GetEncoded(descriptor.GetProperty("digest").GetString()!);
                Assert.IsTrue(files.TryGetValue(path, out byte[]? bytes), path);
                Assert.AreEqual(descriptor.GetProperty("size").GetInt64(), (long)bytes!.Length);
            }
        }

        Microsoft.DotNet.Cli.Utils.CommandResult Publish(string[] args)
            => new DotnetCommand(Log, args)
                .WithWorkingDirectory(asset.TestRoot)
                .WithEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "0")
                .WithEnvironmentVariable("DOTNET_CONTAINER_INSECURE_REGISTRIES", registry.Address)
                .WithEnvironmentVariable("SOURCE_DATE_EPOCH", "1700000000")
                .WithEnvironmentVariable("XDG_CACHE_HOME", cache)
                .WithEnvironmentVariable("LOCALAPPDATA", cache)
                .WithEnvironmentVariable("HOME", cache)
                .Execute();
    }
}
