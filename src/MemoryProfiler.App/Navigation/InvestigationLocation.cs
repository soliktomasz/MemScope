using MemoryProfiler.App.ViewModels.Objects;

namespace MemoryProfiler.App.Navigation;

public abstract record InvestigationLocation;

public sealed record TypesLocation : InvestigationLocation;

public sealed record TypeLocation(ulong MethodTable) : InvestigationLocation;

public sealed record ObjectReferencesLocation(
    ulong ObjectAddress,
    string ObjectTypeName,
    ReferenceDirection Direction) : InvestigationLocation;

public sealed record GcRootsLocation(
    ulong ObjectAddress,
    string ObjectTypeName) : InvestigationLocation;
