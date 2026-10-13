using CSharpFunctionalExtensions;
using Lasero.Core.Machines;
using Lasero.Core.Materials;

namespace Lasero.Avalonia.Services;

public sealed record MigrationSnapshot(
    int MaterialCount,
    int RecipeCount,
    int KnownMachineCount,
    string Source);

public interface IMigrationInventory
{
    Result<MigrationSnapshot> Load();
}

public sealed class MigrationInventory : IMigrationInventory
{
    public Result<MigrationSnapshot> Load()
    {
        try
        {
            KnownMachineProfile[] machines =
            [
                KnownMachineProfiles.AlgoLaserPixi,
                KnownMachineProfiles.AlgoLaserAlphaMk2_20W,
            ];

            return Result.Success(new MigrationSnapshot(
                MaterialCatalog.Materials.Count,
                MaterialCatalog.Recipes.Count,
                machines.Length,
                "Lasero.Core"));
        }
        catch (Exception exception)
        {
            return Result.Failure<MigrationSnapshot>(
                $"Core data could not be loaded: {exception.Message}");
        }
    }
}
