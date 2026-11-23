namespace SettlementRecon.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => ReconCli.Create().Parse(args).InvokeAsync();
}
