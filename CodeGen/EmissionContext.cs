using PspScript.Semantics;

namespace PspScript.CodeGen;

internal sealed class EmissionContext
{
    public ActorSymbol? CurrentActor { get; init; }
    public StateSymbol? CurrentState { get; init; }
    public string? ActiveMessageParamName { get; init; }
    public string? ActiveMessageName { get; init; }
    public HashSet<string> LocalVariables { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FrameArenaPointerLocals { get; } = new(StringComparer.Ordinal);

    public EmissionContext Clone()
    {
        var copy = new EmissionContext
        {
            CurrentActor = CurrentActor,
            CurrentState = CurrentState,
            ActiveMessageParamName = ActiveMessageParamName,
            ActiveMessageName = ActiveMessageName
        };

        foreach (var v in LocalVariables)
        {
            copy.LocalVariables.Add(v);
        }

        foreach (var f in FrameArenaPointerLocals)
        {
            copy.FrameArenaPointerLocals.Add(f);
        }

        return copy;
    }
}
