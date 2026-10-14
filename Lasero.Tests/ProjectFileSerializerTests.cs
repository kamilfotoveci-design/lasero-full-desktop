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
        store.SwitchAccount("account-a");
        store.Save(new LaseroProjectFile { Name = "První" });
        store.Save(new LaseroProjectFile { Name = "Druhý" });

        Assert.True(store.HasSnapshot);
        Assert.Equal("Druhý", store.TryLoad()!.Name);

        store.Discard();
        Assert.False(store.HasSnapshot);
    }

    [Fact]
    public void RecoveryStore_SwitchingAccountsKeepsSnapshotsIsolated()
    {
        var store = new ProjectRecoveryStore(_directory);
        store.SwitchAccount("account-a");
        store.Save(new LaseroProjectFile { Name = "První účet" });

        store.SwitchAccount("account-b");
        Assert.False(store.HasSnapshot);
        store.Save(new LaseroProjectFile { Name = "Druhý účet" });

        store.SwitchAccount("account-a");
        Assert.Equal("První účet", store.TryLoad()!.Name);
        store.Discard();

        store.SwitchAccount("account-b");
        Assert.Equal("Druhý účet", store.TryLoad()!.Name);
    }

    [Fact]
    public void RecoveryStore_LeavesLegacySharedSnapshotUntouchedAndHidesItWithoutAccount()
    {
        var legacyPath = Path.Combine(_directory, "autosave.lasero");
        ProjectFileSerializer.Save(legacyPath, new LaseroProjectFile { Name = "Starší záloha" });
        var store = new ProjectRecoveryStore(_directory);

        Assert.False(store.HasSnapshot);
        store.SwitchAccount("account-a");
        Assert.False(store.HasSnapshot);
        store.Save(new LaseroProjectFile { Name = "Účet A" });
        store.SwitchAccount(null);
        Assert.False(store.HasSnapshot);
        Assert.Null(store.TryLoad());
        store.Discard();

        Assert.True(File.Exists(legacyPath));
        Assert.Equal("Starší záloha", ProjectFileSerializer.Load(legacyPath).Name);
        store.SwitchAccount("account-a");
        Assert.Equal("Účet A", store.TryLoad()!.Name);
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

    // A source image that vanished from disk used to make every Save and autosave throw, so one
    // moved file blocked saving the whole project. The project is now saved, the picture is reported
    // as missing, and its path is kept so it is found again if the file reappears.
    [Fact]
    public void Save_WithMissingRasterSource_SucceedsReportsItAndKeepsThePath()
    {
        var projectPath = Path.Combine(_directory, "safe.lasero");
        var missingPath = Path.Combine(_directory, "missing.png");
        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "Původní" });
        var project = new LaseroProjectFile
        {
            Name = "S chybějícím obrázkem",
            Objects =
            [
                new ProjectObject { Name = "Chybějící", RasterFilePath = missingPath },
                new ProjectObject { Name = "Křivka" },
            ]
        };

        var warnings = ProjectFileSerializer.Save(projectPath, project);

        Assert.Contains("Chybějící", Assert.Single(warnings));
        var loaded = ProjectFileSerializer.Load(projectPath, Path.Combine(_directory, "cache-missing"));
        Assert.Equal("S chybějícím obrázkem", loaded.Name);
        Assert.Equal(2, loaded.Objects.Count);
        Assert.Equal(missingPath, loaded.Objects[0].RasterFilePath);
        Assert.Contains(loaded.LoadWarnings, warning => warning.Contains("Chybějící", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Save_WithKeepBackup_KeepsThePreviousVersionBesideTheProject()
    {
        var projectPath = Path.Combine(_directory, "versioned.lasero");
        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "První" });

        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "Druhý" }, keepBackup: true);

        Assert.Equal("Druhý", ProjectFileSerializer.Load(projectPath).Name);
        Assert.Equal("První", ProjectFileSerializer.Load(projectPath + ".bak").Name);
    }

    [Fact]
    public void Save_WithKeepBackup_DoesNotReplaceAGoodBackupWithAnUnreadableFile()
    {
        var projectPath = Path.Combine(_directory, "damaged.lasero");
        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "Dobrá" });
        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "Střední" }, keepBackup: true);
        File.WriteAllText(projectPath, "toto není projekt");

        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "Nová" }, keepBackup: true);

        Assert.Equal("Dobrá", ProjectFileSerializer.Load(projectPath + ".bak").Name);
    }

    [Fact]
    public void Save_RemovesOldTemporaryFilesLeftByACrashedSave()
    {
        var projectPath = Path.Combine(_directory, "crashy.lasero");
        var stale = Path.Combine(_directory, ".crashy.lasero.0123456789abcdef.tmp");
        var fresh = Path.Combine(_directory, ".crashy.lasero.fedcba9876543210.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile { Name = "Projekt" });

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh)); // may belong to a save that is still running
    }

    [Fact]
    public void Save_KeepsAssetExtensionAndRecognisesImagesWithoutOne()
    {
        var tiffPath = Path.Combine(_directory, "scan.tiff");
        var noExtensionPath = Path.Combine(_directory, "picture");
        var unknownPath = Path.Combine(_directory, "blob");
        File.WriteAllBytes(tiffPath, [1, 2, 3]);
        File.WriteAllBytes(noExtensionPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        File.WriteAllBytes(unknownPath, [9, 9, 9, 9]);
        var projectPath = Path.Combine(_directory, "assets.lasero");

        ProjectFileSerializer.Save(projectPath, new LaseroProjectFile
        {
            Objects =
            [
                new ProjectObject { Name = "A", RasterFilePath = tiffPath },
                new ProjectObject { Name = "B", RasterFilePath = noExtensionPath },
                new ProjectObject { Name = "C", RasterFilePath = unknownPath },
            ]
        });

        using var archive = ZipFile.OpenRead(projectPath);
        Assert.NotNull(archive.GetEntry("assets/raster-0000.tiff"));
        Assert.NotNull(archive.GetEntry("assets/raster-0001.png"));
        Assert.NotNull(archive.GetEntry("assets/raster-0002.bin"));
    }

    [Fact]
    public void NonFiniteNumbersDoNotBlockSaveAndTheCorruptObjectIsDroppedOnLoad()
    {
        var good = new ProjectObject
        {
            Name = "Dobrý",
            Shapes = [new ProjectShape { Points = [new Position(0, 0, 0), new Position(5, 5, 0)] }],
        };
        var poisoned = new ProjectObject
        {
            Name = "Poškozený",
            Shapes = [new ProjectShape { Points = [new Position(0, 0, 0), new Position(double.NaN, 5, 0)] }],
        };
        var infiniteTransform = new ProjectObject
        {
            Name = "Nekonečný",
            Transform = new ObjectTransform(double.PositiveInfinity, 0, 0, 1, 1),
        };
        var path = Path.Combine(_directory, "nan.lasero");

        ProjectFileSerializer.Save(path, new LaseroProjectFile { Objects = [good, poisoned, infiniteTransform] });
        var loaded = ProjectFileSerializer.Load(path);

        Assert.Equal("Dobrý", Assert.Single(loaded.Objects).Name);
        Assert.Equal(2, loaded.LoadWarnings.Count);
        Assert.Contains(loaded.LoadWarnings, warning => warning.Contains("Poškozený", StringComparison.Ordinal));
        Assert.Contains(loaded.LoadWarnings, warning => warning.Contains("Nekonečný", StringComparison.Ordinal));
    }

    [Fact]
    public void NonFiniteVectorHandleDropsTheObject()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(1, 2, 0)),
            new VectorNode(new Position(8, 9, 0), new Position(double.NaN, 8, 0), null, VectorNodeType.Corner),
        ]);

        var restored = ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(
            new LaseroProjectFile { Objects = [new ProjectObject { Name = "Křivka", VectorPath = path }] }));

        Assert.Empty(restored.Objects);
        Assert.Single(restored.LoadWarnings);
    }

    [Fact]
    public void NonFiniteLayerSettingsAreResetToModeDefaults()
    {
        var restored = ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(new LaseroProjectFile
        {
            Layers = [new ProjectLayer { Name = "Rez", Mode = LayerMode.Cut, Speed = double.NaN, Power = double.PositiveInfinity, FillLineIntervalMm = 0.1 }],
        }));

        var layer = Assert.Single(restored.Layers);
        Assert.Equal(350, layer.Speed);
        Assert.Equal(95, layer.Power);
        Assert.Single(restored.LoadWarnings);
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

        Assert.Equal(ProjectFileSerializer.CurrentVersion, loaded.Version);
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

        Assert.Equal(ProjectFileSerializer.CurrentVersion, loaded.Version);
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

        Assert.Equal(ProjectFileSerializer.CurrentVersion, loaded.Version);
        Assert.Equal(layerId, loaded.Objects[0].Shapes[0].LayerId);
    }

    [Fact]
    public void ProjectFromNewerVersionIsRejectedInsteadOfSilentlyDowngraded()
    {
        var json = $$"""{"Version":{{ProjectFileSerializer.CurrentVersion + 1}},"Name":"Budoucí projekt"}""";

        var error = Assert.Throws<InvalidDataException>(() => ProjectFileSerializer.Deserialize(json));
        Assert.Contains("novější verzí", error.Message, StringComparison.Ordinal);
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
