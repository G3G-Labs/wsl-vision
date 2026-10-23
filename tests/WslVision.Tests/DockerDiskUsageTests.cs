using System.Text.Json;
using WslVision.Models;
using Xunit;

namespace WslVision.Tests;

public class DockerDiskUsageTests
{
    // Trecho real de GET /system/df, reduzido aos campos que o app usa.
    private const string Sample = """
        {
          "LayersSize": 1000,
          "Images": [
            { "Size": 600, "SharedSize": 100, "Containers": 1 },
            { "Size": 300, "SharedSize": 100, "Containers": 0 }
          ],
          "Containers": [
            { "SizeRw": 50, "State": "running" },
            { "SizeRw": 20, "State": "exited" },
            { "SizeRw": 5, "State": "created" }
          ],
          "Volumes": [
            { "UsageData": { "Size": 400, "RefCount": 1 } },
            { "UsageData": { "Size": 70, "RefCount": 0 } },
            { "UsageData": { "Size": -1, "RefCount": 0 } }
          ],
          "BuildCache": [
            { "Size": 10, "InUse": false, "Shared": false },
            { "Size": 30, "InUse": true, "Shared": false },
            { "Size": 40, "InUse": false, "Shared": true }
          ]
        }
        """;

    private static DockerDiskUsage Parse() => JsonSerializer.Deserialize<DockerDiskUsage>(Sample)!;

    // Como o "docker system df": camadas (1000) menos o que a imagem em uso ocupa sozinha (600 - 100).
    [Fact]
    public void Imagens() => Assert.Equal(500, Parse().ReclaimableImages);

    [Fact]
    public void ContainersParados() => Assert.Equal(25, Parse().ReclaimableContainers);

    // Tamanho -1 = o Docker não calculou; não entra na soma.
    [Fact]
    public void VolumesSemUso() => Assert.Equal(70, Parse().ReclaimableVolumes);

    [Fact]
    public void CacheDeBuild() => Assert.Equal(10, Parse().ReclaimableBuildCache);

    [Fact]
    public void RespostaSemListas() => Assert.Equal(0, JsonSerializer.Deserialize<DockerDiskUsage>("{}")!.ReclaimableVolumes);
}
