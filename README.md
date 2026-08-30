# MemScope

MemScope is a cross-platform desktop application for monitoring managed memory in running .NET processes.

Built with .NET and Avalonia, it connects to a process through EventPipe and displays live runtime metrics without modifying the target application.

## Features

- Discover and attach to running .NET processes
- Monitor managed heap size and allocation rate
- Inspect Gen 0, Gen 1, and Gen 2 sizes and collection counts
- Track large object heap, pinned object heap, and promoted bytes
- Capture heap-bearing dumps from a live profiling session
- Open captured dumps and browse managed heap types with sorting and filters
- Inspect object instances of a type and follow incoming and outgoing references between heap objects
- Disconnect safely when profiling is complete

Opening captured dumps and offline heap analysis are supported through the snapshot type browser, instance lists, and reference navigation.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A target .NET process accessible to the current user

## Getting Started

```bash
git clone https://github.com/soliktomasz/MemScope.git
cd MemScope
dotnet run --project src/MemoryProfiler.App
```

Select **Attach to Process**, choose a running .NET application, and select **Start profiling**.

## Development

Build the solution:

```bash
dotnet build MemoryProfiler.sln
```

Run the test suite:

```bash
dotnet test MemoryProfiler.sln
```

The solution separates the desktop UI, diagnostics integration, shared contracts, analysis, and storage into projects under `src/`. Tests mirror these projects under `tests/`.

## License

Licensed under the [MIT License](LICENSE.md).
