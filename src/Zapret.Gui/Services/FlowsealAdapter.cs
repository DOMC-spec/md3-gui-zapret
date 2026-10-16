using Zapret.Core;

namespace Zapret.Gui.Services;

public interface IFlowsealAdapter
{
    Task<DistributionDescription> DescribeAsync(string root);
    Task<ServiceSnapshot> ReadStateAsync(string root);
}

/// <summary>The GUI's read-only Flowseal boundary. Mutation capabilities are enabled by later parity work.</summary>
public sealed class FlowsealAdapter(ICommandRunner runner, Func<Task<ServiceSnapshot>>? readServiceState = null) : IFlowsealAdapter
{
    private readonly FlowsealDistributionAdapter distribution = new();
    private readonly ZapretService service = new(runner);
    public Task<DistributionDescription> DescribeAsync(string root) => Task.Run(() => distribution.Describe(root));
    public Task<ServiceSnapshot> ReadStateAsync(string root) => readServiceState?.Invoke() ?? service.ReadAsync();
}