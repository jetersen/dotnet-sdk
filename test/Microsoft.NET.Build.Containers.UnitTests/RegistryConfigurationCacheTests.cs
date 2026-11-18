// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Microsoft.NET.Build.Containers.UnitTests;

[TestClass]
public class RegistryConfigurationCacheTests
{
    public TestContext TestContext { get; }
    public RegistryConfigurationCacheTests(TestContext testContext) => TestContext = testContext;

    [TestMethod]
    public async Task Mutable_tags_are_resolved_again_while_unchanged_configurations_are_reused_independently()
    {
        string identity = Guid.NewGuid().ToString("N");
        byte[][] configurations = Enumerable.Range(0, 2).Select(i => JsonSerializer.SerializeToUtf8Bytes(new
        {
            architecture = "amd64", os = "linux",
            config = new { Env = new[] { $"CACHE_VALUE={identity}-{i}" } },
            rootfs = new { type = "layers", diff_ids = Array.Empty<string>() }
        })).ToArray();
        string[] digests = configurations.Select(Hash).ToArray();
        ManifestResponse[] manifests = configurations.Select((bytes, i) =>
        {
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new ManifestV2
            {
                SchemaVersion = 2, MediaType = SchemaTypes.OciManifestV1,
                Config = new(SchemaTypes.OciImageConfigV1, bytes.Length, digests[i]), Layers = []
            });
            return new ManifestResponse(manifest, Hash(manifest), SchemaTypes.OciManifestV1);
        }).ToArray();
        int current = 0;
        Mock<IRegistryAPI> api = new(MockBehavior.Strict);
        api.Setup(a => a.Manifest.GetAsync("repo", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => manifests[current]);
        for (int i = 0; i < configurations.Length; i++)
        {
            ReadOnlyMemory<byte> bytes = configurations[i];
            api.Setup(a => a.Blob.GetContentAsync("repo", digests[i], It.IsAny<CancellationToken>())).ReturnsAsync(bytes);
        }
        Mock<IManifestPicker> picker = new(MockBehavior.Strict);
        Registry registry = new("localhost:5000", NullLogger.Instance, api.Object);
        try
        {
            ImageBuilder first = await Read();
            first.AddEnvironmentVariable("CACHE_VALUE", "modified by first image");
            first.Build(DateTime.UnixEpoch);
            ImageBuilder second = await Read();
            Assert.AreEqual(identity + "-0", second.BaseImageConfig.EnvironmentVariables["CACHE_VALUE"]);
            current = 1;
            ImageBuilder third = await Read();
            Assert.AreEqual(identity + "-1", third.BaseImageConfig.EnvironmentVariables["CACHE_VALUE"]);
            Assert.AreNotEqual(second.BaseImageManifestDigest, third.BaseImageManifestDigest);
            Assert.AreEqual(digests[1], third.ManifestConfigDigest);
            api.Verify(a => a.Manifest.GetAsync("repo", "latest", It.IsAny<CancellationToken>()), Times.Exactly(3));
            foreach (string digest in digests)
                api.Verify(a => a.Blob.GetContentAsync("repo", digest, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            foreach (string digest in digests)
            {
                string path = Path.Combine(ContentStore.ContentRoot, DigestUtils.GetEncoded(digest) + ".config.json");
                File.Delete(path);
                File.Delete(path + ".download.lock");
            }
        }
        Task<ImageBuilder> Read() => registry.GetImageManifestAsync("repo", "latest", "linux-x64", picker.Object, TestContext.CancellationToken);
    }

    private static string Hash(byte[] bytes) => DigestUtils.FormatSha256Digest(Convert.ToHexStringLower(SHA256.HashData(bytes)));
}
