namespace Ruri.ShaderTools.Binding;

/// <summary>A name a binder gives one member of one of the module's structs.</summary>
public readonly record struct BoundMemberName(uint StructType, uint Member, string Name);

/// <summary>
/// Naming facts a host can only state with the module in hand. Binders run after constant-buffer structuring and before
/// symbol injection: a binder may add to the symbol table (whatever the table expresses is then named by the pipeline's own
/// planners) and name struct members the table cannot address. A name that contradicts the planners is an error, never a
/// silent override. The decompiler ships no binder; every engine-specific fact enters here. Called concurrently.
/// </summary>
public interface IModuleSymbolBinder
{
    void Bind(ModuleLayout module, SerializedProgramData symbols, ICollection<BoundMemberName> names);
}
