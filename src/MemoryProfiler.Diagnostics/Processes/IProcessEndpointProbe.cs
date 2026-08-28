using Microsoft.Diagnostics.NETCore.Client;

namespace MemoryProfiler.Diagnostics.Processes;

internal interface IProcessEndpointProbe
{
    ValueTask ValidateAsync(int processId, CancellationToken cancellationToken);
}

internal sealed class DiagnosticsClientEndpointProbe : IProcessEndpointProbe
{
    private readonly Action<int> _validate;

    public DiagnosticsClientEndpointProbe()
        : this(processId =>
        {
            _ = new DiagnosticsClient(processId).GetProcessEnvironment();
        })
    {
    }

    internal DiagnosticsClientEndpointProbe(Action<int> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        _validate = validate;
    }

    public async ValueTask ValidateAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var validation = Task.Run(() => _validate(processId));

        await validation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
