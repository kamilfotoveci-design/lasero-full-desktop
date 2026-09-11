using System.IO;
using System.IO.Compression;
using Lasero.App;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Tests;

public sealed class ProjectFileSerializerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-project-tests", Guid.NewGuid().ToString("N"));

    public ProjectFileSerializerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Save_CanOverwriteExistingProject()
    {
        var path = Path.Combine(_directory, "project.lasero");
        ProjectFileSerializer.Save(path, new LaseroProjectFile { Name = "První" });

        ProjectFileSerializer.Save(path, new LaseroProjectFile { Name = "Druhý" });

        Assert.Equal("Druhý", ProjectFileSerializer.Load(path).Name);
    }

    [Fact]
    public void Save_DoesNotLeaveTemporaryFiles()
    {
        var path = Path.Combine(_directory, "project.lasero");

        ProjectFileSerializer.Save(path, new LaseroProjectFile { Name = "Projekt" });

        Assert.Single(Directory.GetFiles(_directory));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_RemainsCompatibleWithPlainJsonProjects()
    {
        var path = Path.Combine(_directory, "legacy.lasero");
        File.WriteAllText(path, ProjectFileSerializer.Serialize(new LaseroProjectFile { Name = "Starší" }));

        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal("Starší", loaded.Name);
    }

    [Fact]
    public void Serialize_PreservesEditableVectorPathData()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(1, 2, 0)),
            new VectorNode(new Position(8, 9, 0), new Position(6, 8, 0), null, VectorNodeType.Smooth),
        ]);
        var project = new LaseroProjectFile
        {
            Objects = [new ProjectObject { Name = "Křivka", VectorPath = path }],
        };

        var restored = ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(project));

        var restoredSubpath = Assert.Single(Assert.IsType<VectorPath>(Assert.Single(restored.Objects).VectorPath).Subpaths);
        Assert.Equal(path.Subpaths[0].IsClosed, restoredSubpath.IsClosed);
        Assert.Equal(path.Subpaths[0].Nodes, restoredSubpath.Nodes);
    }

    [Fact]
    public void RecoveryStore_CanReplaceAndRestoreSnapshot()
    {
        var store = new ProjectRecoveryStore(_directory);
        store.Save(new LaseroProjectFile { Name = "První" });
        store.Save(new LaseroProjectFile { Name = "Druhý" });

        Assert.True(store.HasSnapshot);
        Assert.Equal("Druhý", store.TryLoad()!.Name);

        store.Discard();
        Assert.False(store.HasSnapshot);
    }

    [Fact]
    public void Save_EmbedsRasterAssetAndLoadsWithoutOriginalFile()
    {
        var sourcePath = Path.Combine(_directory, "source.png");
        var projectPath = Path.Combine(_directory, "embedded.lasero");
        var cachePath = Path.Combine(_directory, "cache");
        var expectedBytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(sourcePath, expectedBytes);
        var project = new LaseroProjectFile
        {
            Objects =
            [
                new ProjectObject
                {
                    Name = "Logo",
                    RasterFilePath = sourcePath,
                    RasterOptions = new Lasero.Core.Import.RasterImportOptions(),
                }
            ]
        };

        ProjectFileSerializer.Save(projectPath, project);
        File.Delete(sourcePath);

        var loaded = ProjectFileSerializer.Load(projectPath, cachePath);
        var extractedPath = loaded.Objects[0].RasterFilePath;
        Assert.NotNull(extractedPath);
        Assert.True(File.Exists(extractedPath));
        Assert.Equal(expectedBytes, File.ReadAllBytes(extractedPath));

        using var archive = ZipFile.OpenRead(projectPath);
        using var reader = new StreamReader(archive.GetEntry("project.json")!.Open());
        var manifest = reader.ReadToEnd();
        Assert.DoesNotContain(sourcePath, manifest, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(archive.GetEntry("assets/raster-0000.png"));
    }

    [Fact]
    public void Save_EmbedsOriginalRasterAssetAlongsideBackgroundRemovedFile()
    {
        var currentPath = Path.Combine(_directory, "nobg.png");
        var originalPath = Path.Combine(_directory, "original.png");
        var projectPath = Path.Combine(_directory, "background-removed.lasero");
        var cachePath = Path.Combine(_directory, "cache");
        var currentBytes = new byte[] { 9, 8, 7 };
        var originalBytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(currentPath, currentBytes);
        File.WriteAllBytes(originalPath, originalBytes);
        var project = new LaseroProjectFile
        {
            Objects =
            [
                new ProjectObject
                {
                    Name = "Foto",
                    RasterFilePath = currentPath,
                    OriginalRasterFilePath = originalPath,
                    RasterOptions = new Lasero.Core.Import.RasterImportOptions(),
                }
            ]
        };

        ProjectFileSerializer.Save(projectPath, project);
        File.Delete(currentPath);
        File.Delete(originalPath);

        var loaded = ProjectFileSerializer.Load(projectPath, cachePath);
        var loadedObject = loaded.Objects[0];

        Assert.NotNull(loadedObject.RasterFilePath);
        Assert.NotNull(loadedObject.OriginalRasterFilePath);
        Assert.True(File.Exists(loadedObject.RasterFilePath));
        Assert.True(File.Exists(loadedObject.OriginalRasterFilePath));
        Assert.Equal(currentBytes, File.ReadAllBytes(loadedObject.RasterFilePath));
        Assert.Equal(originalBytes, File.ReadAllBytes(loadedObject.OriginalRasterFilePath));
        Assert.NotEqual(loadedObject.RasterFilePath, loadedObject.OriginalRasterFilePath);
    }

    [Fact]
    public void Save_WithoutBackgroundRemoval_LeavesOriginalRasterFilePathNull()
    {
        var sourcePath = Path.Combine(_directory, "plain.png");
        var projectPath = Path.Combine(_directory, "plain.lasero");
        File.WriteAllBytes(sourcePath, [1]);
        var project = new LaseroProjectFile
        {
            Objects = [new ProjectObject { Name = "Foto", RasterFilePath = sourcePath }]
        };

        ProjectFileSerializer.Save(projectPath, project);
        var loaded = ProjectFileSerializer.Load(projectPath, Path.Combine(_directory, "cache2"));

        Assert.Null(loaded.Objects[0].OriginalRasterFilePath);
    }

    [Fact]
    public void FailedAssetSave_PreservesPreviousProject()
    {
        var projectPath = Path.Combine(_directory, "safe.lasero");
        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "Původní" });
        var invalid = new LaseroProjectFile
        {
            Name = "Poškozený",
            Objects = [new ProjectObject { Name = "Chybějící", RasterFilePath = Path.Combine(_directory, "missing.png") }]
        };

        Assert.Throws<FileNotFoundException>(() => ProjectFileSerializer.Save(projectPath, invalid));

        Assert.Equal("Původní", ProjectFileSerializer.Load(projectPath).Name);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void SaveAndLoadPreservesPhysicalJobPlacement()
    {
        var path = Path.Combine(_directory, "placement.lasero");
        var project = new LaseroProjectFile
        {
            Name = "Umístěný projekt",
            Placement = new ProjectJobPlacement
            {
                Mode = JobPlacementMode.CurrentPosition,
                Anchor = JobOriginAnchor.TopLeft, ReferenceX = 125.5, ReferenceY = 84.25,
            },
        };

        ProjectFileSerializer.Save(path, project);
        var loaded = ProjectFileSerializer.Load(path);
        Assert.NotNull(loaded.Placement);
        Assert.Equal(JobPlacementMode.CurrentPosition, loaded.Placement.Mode);
        Assert.Equal(JobOriginAnchor.TopLeft, loaded.Placement.Anchor);
        Assert.Equal(125.5, loaded.Placement.ReferenceX);
        Assert.Equal(84.25, loaded.Placement.ReferenceY);
    }

    [Fact]
    public void OlderPlacementWithoutModeMigratesToCurrentPosition()
    {
        const string json = """
            {
              "Version": 3,
              "Name": "Starší projekt",
              "Objects": [],
              "Layers": [],
              "Placement": {
                "Anchor": "TopLeft",
                "ReferenceX": 20,
                "ReferenceY": 30
              }
            }
            """;

        var loaded = ProjectFileSerializer.Deserialize(json);

        Assert.NotNull(loaded.Placement);
        Assert.Equal(JobPlacementMode.CurrentPosition, loaded.Placement.Mode);
    }

    [Fact]
    public void SaveAndLoadPreservesStableLayerIdsAndManufacturingOrder()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var path = Path.Combine(_directory, "layer-order.lasero");
        var project = new LaseroProjectFile
        {
            Layers =
            [
                new ProjectLayer { Id = firstId, Name = "Nejdřív čára", Color = new RgbColor(220, 40, 40), Mode = LayerMode.Cut },
                new ProjectLayer { Id = secondId, Name = "Potom výplň", Color = new RgbColor(30, 80, 210), Mode = LayerMode.Fill },
            ],
        };

        ProjectFileSerializer.Save(path, project);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal(6, loaded.Version);
        Assert.Collection(loaded.Layers,
            layer => Assert.Equal((firstId, "Nejdřív čára"), (layer.Id, layer.Name)),
            layer => Assert.Equal((secondId, "Potom výplň"), (layer.Id, layer.Name)));
    }

    // MaterialLabel was added after v6 project files were already in the wild, so it has to survive
    // a round trip and a file that predates it has to load with no label rather than fail.
    [Fact]
    public void SaveAndLoadPreservesLayerMaterialLabel()
    {
        var path = Path.Combine(_directory, "layer-material.lasero");
        var project = new LaseroProjectFile
        {
            Layers =
            [
                new ProjectLayer { Name = "Rez", Color = RgbColor.Red, Mode = LayerMode.Cut, MaterialLabel = "Překližka 3 mm" },
                new ProjectLayer { Name = "Ručně", Color = RgbColor.Red, Mode = LayerMode.Cut },
            ],
        };

        ProjectFileSerializer.Save(path, project);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal("Překližka 3 mm", loaded.Layers[0].MaterialLabel);
        Assert.Null(loaded.Layers[1].MaterialLabel);
    }

    [Fact]
    public void SaveAndLoadPreservesShapeLayerId()
    {
        var layerId = Guid.NewGuid();
        var path = Path.Combine(_directory, "shape-layer-id.lasero");
        var project = new LaseroProjectFile
        {
            Layers = [new ProjectLayer { Id = layerId, Name = "Obrys", Color = RgbColor.Red, Mode = LayerMode.Cut }],
            Objects =
            [
                new ProjectObject
                {
                    Shapes =
                    [
                        new ProjectShape
                        {
                            LayerId = layerId,
                            LayerColor = RgbColor.Red,
                            PreferredMode = LayerMode.Cut,
                            IsClosed = false,
                        },
                    ],
                },
            ],
        };

        ProjectFileSerializer.Save(path, project);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal(6, loaded.Version);
        Assert.Equal(layerId, loaded.Objects[0].Shapes[0].LayerId);
    }

    [Fact]
    public void SaveAndLoadPreservesCompoundGeometryIdentity()
    {
        var geometrySetId = Guid.NewGuid();
        var path = Path.Combine(_directory, "compound-geometry-id.lasero");
        var project = new LaseroProjectFile
        {
            Objects =
            [
                new ProjectObject
                {
                    Shapes =
                    [
                        new ProjectShape
                        {
                            GeometrySetId = geometrySetId,
                            LayerColor = RgbColor.Black,
                            PreferredMode = LayerMode.Cut,
                            IsClosed = true,
                        },
                    ],
                },
            ],
        };

        ProjectFileSerializer.Save(path, project);
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal(geometrySetId, loaded.Objects[0].Shapes[0].GeometrySetId);
    }

    [Fact]
    public void LegacyShapeWithoutLayerIdMigratesByColor()
    {
        var layerId = Guid.NewGuid();
        var json = $$"""
            {
              "Version": 5,
              "Name": "Starší geometrie",
              "Layers": [
                {
                  "Id": "{{layerId}}",
                  "Color": { "R": 220, "G": 40, "B": 40 },
                  "Name": "Obrys",
                  "Mode": "Cut"
                }
              ],
              "Objects": [
                {
                  "Shapes": [
                    {
                      "Points": [],
                      "IsClosed": false,
                      "LayerColor": { "R": 220, "G": 40, "B": 40 },
                      "PreferredMode": "Cut"
                    }
                  ]
                }
              ]
            }
            """;

        var loaded = ProjectFileSerializer.Deserialize(json);

        Assert.Equal(6, loaded.Version);
        Assert.Equal(layerId, loaded.Objects[0].Shapes[0].LayerId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // The OS may briefly retain a ZIP handle after a failed assertion.
        }
    }
}
