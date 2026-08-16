namespace Ruri.ShaderTools.Pipeline;

/// <summary>What a stage tells the schedule to do next.</summary>
internal enum StageVerdict
{
    /// <summary>Run the next stage.</summary>
    Continue,

    /// <summary>Stop here. The context holds whatever partial result exists; the
    /// caller decides what that means.</summary>
    Halt,
}

internal delegate StageVerdict ShaderStage<in TContext>(TContext context);

/// <summary>
/// An ordered list of stages over a shared context.
///
/// The point of this type is that ORDER IS DATA. Previously the running order of
/// a transform lived in the file names (<c>Pass010_…</c>, <c>Pass015_…</c>) and
/// in a hand-maintained call list, so inserting a stage meant renumbering files
/// and the two could silently disagree. Here the schedule is one literal, the
/// stages are named by what they do, and reordering is a one-line edit.
///
/// Zero-allocation at run time: a schedule is built once into a static readonly
/// field, and <see cref="Execute"/> walks an array of delegates. Nothing is
/// allocated per shader.
/// </summary>
internal sealed class StageSchedule<TContext>
{
    private readonly ScheduledStage[] _stages;

    public StageSchedule(params ScheduledStage[] stages) => _stages = stages;

    /// <summary>
    /// Run every stage in order, stopping early on the first
    /// <see cref="StageVerdict.Halt"/>.
    /// </summary>
    /// <returns>
    /// The name of the halting stage, or <see langword="null"/> when the whole
    /// schedule ran. Callers surface this in failure diagnostics so a bail-out
    /// is attributable to a specific stage rather than to "the pipeline".
    /// </returns>
    public string? Execute(TContext context)
    {
        ScheduledStage[] stages = _stages;
        for (int i = 0; i < stages.Length; i++)
        {
            if (stages[i].Run(context) == StageVerdict.Halt)
            {
                return stages[i].Name;
            }
        }
        return null;
    }

    internal readonly struct ScheduledStage
    {
        public ScheduledStage(string name, ShaderStage<TContext> run)
        {
            Name = name;
            Run = run;
        }

        public string Name { get; }
        public ShaderStage<TContext> Run { get; }
    }

    public static ScheduledStage Stage(string name, ShaderStage<TContext> run) => new(name, run);

    /// <summary>Adapter for the common case of a stage that never halts.</summary>
    public static ScheduledStage Stage(string name, Action<TContext> run)
        => new(name, context => { run(context); return StageVerdict.Continue; });
}
