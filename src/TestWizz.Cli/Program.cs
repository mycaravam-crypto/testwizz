using TestWizz.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return new CliApp(Console.Out, Console.Error, Environment.CurrentDirectory).Run(args, cancellation.Token);
