// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.Security.Cryptography;

namespace Microsoft.NET.Build.Containers.IntegrationTests;

[TestClass]
public class ApplicationLayerIncrementalTests : SdkTest
{
    [TestMethod]
    // Windows resolves Local AppData through the known folder API before LOCALAPPDATA.
    // Keep this test's publishes away from the shared user cache.
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux | OperatingSystems.OSX)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deterministic_layer_reuse_still_resolves_mutable_bases_and_can_be_disabled(bool useMultithreading)
    {
        await using ContainerTestRegistry registry = new();
        var asset = TestAssetsManager.CopyTestAsset("HelloWorld", identifier: useMultithreading.ToString()).WithSource();
        string archive = Path.Combine(asset.TestRoot, "image.tar");
        string cache = Directory.CreateDirectory(Path.Combine(asset.TestRoot, "cache")).FullName;
        string[] arguments = [
            "publish", "-t:PublishContainer", "-r", "linux-x64", "-v:normal", $"-mt:{useMultithreading}",
            "-p:EnableSdkContainerSupport=true", "-p:UseAppHost=false", "-p:SelfContained=false",
            $"-p:ContainerBaseImage={registry.Address}/empty:latest",
            "-p:ContainerRepository=layer-cache-test", "-p:ContainerImageTag=test",
            "-p:ContainerArchiveNoIncremental=true", $"-p:ContainerArchiveOutputPath={archive}"
        ];
        Publish(arguments).Should().Pass();
        string store = Path.Combine(OperatingSystem.IsMacOS() ? Path.Combine(cache, "Library", "Caches") : cache,
            "dotnet", "Containers", "Content");
        string applicationLayer = Directory.GetFiles(store, "*.tar.gz").Single();
        Assert.HasCount(1, Directory.GetFiles(store, "layer-input-*.json"));
        DateTime sentinel = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(applicationLayer, sentinel);
        string[] originalContent = ReadArchiveContent();

        // The image is rebuilt, so this exercises layer reuse rather than an archive hit.
        var unchanged = Publish(arguments);
        unchanged.Should().Pass();
        Assert.Contains("Building image", unchanged.StdOut!);
        Assert.AreEqual(sentinel, File.GetLastWriteTimeUtc(applicationLayer));
        Assert.AreSequenceEqual(originalContent, ReadArchiveContent());

        registry.ChangeBaseManifests("changed");
        Publish(arguments).Should().Pass();
        Assert.AreEqual(3, registry.ConfigurationRequests);
        Assert.AreEqual(sentinel, File.GetLastWriteTimeUtc(applicationLayer));

        Publish([.. arguments, "-p:ContainerLayerNoIncremental=true"]).Should().Pass();
        Assert.AreNotEqual(sentinel, File.GetLastWriteTimeUtc(applicationLayer));
        Assert.AreEqual(4, registry.ConfigurationRequests);

        registry.ChangeBaseManifests("new-config", changeConfiguration: true);
        Publish(arguments).Should().Pass();
        Assert.AreEqual(5, registry.ConfigurationRequests);
        Assert.Contains("CONFIG_VARIANT=new-config", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(archive, TestContext.CancellationToken)));
        Assert.IsEmpty(Directory.GetFiles(store, "*.tmp"));

        string[] ReadArchiveContent()
        {
            using FileStream stream = File.OpenRead(archive);
            using TarReader reader = new(stream);
            List<string> content = [];
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.DataStream is { } data)
                {
                    content.Add(entry.Name + ":" + Convert.ToHexString(SHA256.HashData(data)));
                }
            }
            return content.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        Microsoft.DotNet.Cli.Utils.CommandResult Publish(string[] args)
            => new DotnetCommand(Log, args)
                .WithWorkingDirectory(asset.TestRoot)
                .WithEnvironmentVariable("DOTNET_CONTAINER_INSECURE_REGISTRIES", registry.Address)
                .WithEnvironmentVariable("SOURCE_DATE_EPOCH", "1700000000")
                .WithEnvironmentVariable("XDG_CACHE_HOME", cache)
                .WithEnvironmentVariable("LOCALAPPDATA", cache)
                .WithEnvironmentVariable("HOME", cache)
                .Execute();
    }

}
