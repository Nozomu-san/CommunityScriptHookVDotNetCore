using Alloc8orStandardNatives.Source;

namespace StandardGameOperations.Source;

public enum GameProcessKind
{
    Unsupported = -1,
    Legacy = 0,
    Enhanced = 1
}

public readonly record struct GameEnvironmentInfo(
    GameProcessKind ProcessKind,
    int Build,
    string ExecutablePath,
    string ProductVersion)
{
    public bool IsSupported =>
        ProcessKind is GameProcessKind.Legacy or GameProcessKind.Enhanced &&
        Build >= 0;
}

public interface IGameOperations
{
    GameEnvironmentInfo Current { get; }
    bool IsSupported { get; }
}

internal sealed class GameOperations(IGameBuildService gameBuild) :
    IGameOperations
{
    private readonly IGameBuildService _gameBuild =
        gameBuild ?? throw new ArgumentNullException(nameof(gameBuild));

    public GameEnvironmentInfo Current
    {
        get
        {
            GameBuildInfo current = _gameBuild.Current;
            return new(
                current.Edition switch
                {
                    GameEdition.Legacy => GameProcessKind.Legacy,
                    GameEdition.Enhanced => GameProcessKind.Enhanced,
                    _ => GameProcessKind.Unsupported
                },
                current.Build,
                current.ExecutablePath,
                current.ProductVersion);
        }
    }

    public bool IsSupported => Current.IsSupported;
}